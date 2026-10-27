#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using ProtocolRole = Mahjong.Contracts.ProtocolRole;
using RequestScope = Mahjong.Contracts.RequestScope;
using RequestScopeId = Mahjong.Contracts.RequestScopeId;
using RoomId = Mahjong.Contracts.RoomId;
using SessionId = Mahjong.Contracts.SessionId;
using ConnectionEpoch = Mahjong.Contracts.ConnectionEpoch;
using OwnerEpoch = Mahjong.Contracts.OwnerEpoch;
using BindingEpoch = Mahjong.Contracts.BindingEpoch;
using StreamBaseline = Mahjong.Contracts.StreamBaseline;
using StateVersion = Mahjong.Contracts.StateVersion;
using EventSeq = Mahjong.Contracts.EventSeq;
using StreamCursor = Mahjong.Contracts.StreamCursor;
using StreamVersion = Mahjong.Contracts.StreamVersion;
using ProjectionSeq = Mahjong.Contracts.ProjectionSeq;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;
using ProtocolCommand = Mahjong.Contracts.ProtocolCommand;
using CommandKind = Mahjong.Contracts.CommandKind;
using RequestId = Mahjong.Contracts.RequestId;
using CommandPayloadFactory = Mahjong.Contracts.CommandPayloadFactory;
using ProtocolActionType = Mahjong.Contracts.ProtocolActionType;
using SleeveBloomChoice = Mahjong.Contracts.SleeveBloomChoice;
using ReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using ReceiptLookupStatus = Mahjong.Contracts.ReceiptLookupStatus;
using IdempotencyKey = Mahjong.Contracts.IdempotencyKey;
using WireEventType = Mahjong.Contracts.Wire.EventType;

namespace Mahjong.Unity.Tests.PlayMode
{
    [TestFixture]
    public sealed class TablePlayModeTests
    {
        private Canvas? _canvas;
        private TablePresenter? _presenter;
        private PlayModeScriptedTableChannel? _channel;
        private InMemoryOutboxStore? _outbox;
        private TableSession? _session;
        private TableSessionOperationQueue? _queue;
        private SessionCoordinator? _coordinator;
        private MainThreadDispatcher? _dispatcher;
        private MockNavigation? _mockNav;

        [SetUp]
        public void SetUp()
        {
            UiThemeProvider.EnsureLineBreakingRules();
            UiInputLifecycle.ResetForTest();
            _mockNav = new MockNavigation();
        }

        [TearDown]
        public void TearDown()
        {
            if (_coordinator != null)
            {
                _ = _coordinator.ShutdownAsync("TEST_TEARDOWN");
            }
        }

        private static void CaptureScreen(string fileName, int width = 1280, int height = 720)
        {
            TestScreenshotHelper.CaptureFrame(fileName, width, height);
        }

        private IEnumerator LoadAndBindTableSceneAsync(bool hasDecision = true)
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var loadTask = root.Router.LoadAsync(SceneId.Table);
            while (!loadTask.IsCompleted)
            {
                yield return null;
            }
            Assert.IsTrue(loadTask.Result, "Failed to load Table scene");
            yield return null;
            Canvas.ForceUpdateCanvases();

            _presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(_presenter, "TablePresenter must exist in Table scene");
            _canvas = _presenter!.GetComponentInParent<Canvas>();
            Assert.IsNotNull(_canvas, "Canvas must exist");

            // Setup real client network stack
            _channel = new PlayModeScriptedTableChannel();
            _outbox = new InMemoryOutboxStore();
            _session = new TableSession(_channel, _outbox, new TableSessionOptions());
            _queue = new TableSessionOperationQueue(_session);
            _dispatcher = root.Dispatcher;
            _coordinator = new SessionCoordinator(_session, _queue, _channel, _dispatcher, new AppConfig(), generation: 1);

            _ = _queue.OpenAsync(new TableSessionOpenRequest("ticket_1", "client_1"), CancellationToken.None);
            while (_session.State != TableConnectionState.Handshaking) yield return null;

            var welcome = PlayModeScriptedTableChannel.CreateWelcome("session_table_1", "room_table_1", Role.Player, 0, bindingEpoch: 1);
            _channel.EnqueueEnvelope(welcome);
            var pump = _session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            var syncCorrelationId = _channel.SentEnvelopes[_channel.SentEnvelopes.Count - 1].CorrelationId;
            var sync = CreatePlayerBaselineEnvelope("session_table_1", "room_table_1", 0, correlationId: syncCorrelationId, hasDecision: hasDecision);
            _channel.EnqueueEnvelope(sync);
            pump = _session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            Assert.AreEqual(TableConnectionState.Ready, _session.State, "Session must reach Ready state after sync");

            _presenter.Bind(_coordinator, _mockNav, _coordinator, () => _mockNav!.OpenSettlement());

            for (int i = 0; i < 30 && (_presenter.LatestView == null || !_presenter.LatestView.Identity.IsPrivateAuthorized); i++)
            {
                _dispatcher.Update();
                yield return null;
            }

            Canvas.ForceUpdateCanvases();
        }

        private SessionViewState FormalProbeView(PlayerSnapshot snap, bool baseline = false, TableConnectionState state = TableConnectionState.Ready)
        {
            return new SessionViewState(1, state, _session!.Binding, snap, null, null, null, null, baseline);
        }

        private static Envelope CreatePlayerBaselineEnvelope(
            string sessionId = "session_table_1",
            string roomId = "room_table_1",
            int seatId = 0,
            ulong stateVersion = 1,
            ulong eventSeq = 101,
            bool hasDecision = true,
            string? correlationId = null)
        {
            var snap = new PlayerSnapshot
            {
                SessionId = sessionId,
                RoundId = "round_1",
                StateVersion = stateVersion,
                EventSeq = eventSeq,
                Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                PublicView = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = 68,
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0,
                        LastDiscard = new DiscardView { TileId = 36, TurnNumber = 1, Tsumogiri = true }
                    },
                    Session = new PublicSessionSummary { Finished = false },
                    SettlementRoundId = string.Empty
                },
                PrivateView = new PlayerPrivateView
                {
                    Seat = seatId,
                    Cheat = new PrivateCheatView
                    {
                        CheatPoints = 100,
                        TileMark = new TileMarkView
                        {
                            SelectableTileIds = { 0, 2, 4, 6, 8, 10, 12, 14 }
                        },
                        Preparation = new ShufflePreparationView
                        {
                            Target = 0,
                            SeeThrough = false,
                            SeeThroughEnabled = true,
                            SeeThroughCost = 10,
                            DeadlineRemainingMs = 15000
                        }
                    }
                }
            };

            // Dora indicators: 5万 (16), 1筒 (72)
            snap.PublicView.Board.DoraIndicators.Add(16);
            snap.PublicView.Board.DoraIndicators.Add(72);

            // 4 Players
            for (int i = 0; i < 4; i++)
            {
                snap.PublicView.Board.Players.Add(new PlayerPublicView
                {
                    Seat = i,
                    HandCount = 13,
                    Riichi = i == 2
                });
                snap.PublicView.Board.Scores.Add(new ScoreView
                {
                    Seat = i,
                    Score = 25000
                });
            }

            snap.PublicView.Board.Players[1].Discards.Add(new DiscardView { TileId = 36, Tsumogiri = true });
            snap.PublicView.Board.Players[2].Discards.Add(new DiscardView { TileId = 40, RiichiDiscard = true });
            snap.PublicView.Board.Players[3].Melds.Add(new MeldView
            {
                Type = MeldType.Chi,
                FromSeat = 2,
                CalledTileId = 0,
                TileIds = { 0, 4, 8 }
            });
            snap.PublicView.Board.Players[3].ConcealedMelds.Add(new ConcealedMeldView
            {
                Type = MeldType.ClosedKan,
                TileCount = 4,
                FromSeat = -1
            });

            for (uint t = 0; t < 13; t++)
            {
                snap.PrivateView.ClosedTiles.Add(t * 2);
            }
            snap.PrivateView.DrawnTileId = 26;
            snap.PrivateView.SleeveTiles.Add(124);

            if (hasDecision)
            {
                snap.PrivateView.Decision = new DecisionView
                {
                    DecisionId = "decision_live_1",
                    ControlEpoch = 1,
                    DeadlineRemainingMs = 18000
                };
                snap.PrivateView.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = 4, // DiscardTile
                    OptionId = "discard_0",
                    TileIds = { 0 }
                });
                snap.PrivateView.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = 9, // Tsumo
                    OptionId = "tsumo_win"
                });
                snap.PrivateView.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = 8, // Riichi
                    OptionId = "riichi_decl"
                });
                snap.PrivateView.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = 11, // Pass
                    OptionId = "pass_choice"
                });
            }

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                CorrelationId = correlationId ?? string.Empty,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = seatId,
                BindingEpoch = 1,
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = snap,
                    StateVersion = stateVersion,
                    EventSeq = eventSeq
                }
            };
        }

        private static Envelope CreatePlayerPushEnvelope(
            string sessionId = "session_table_1",
            string roomId = "room_table_1",
            int seatId = 0,
            ulong stateVersion = 2,
            ulong eventSeq = 102,
            ulong projectionSeq = 2,
            bool isFullSnapshot = false,
            bool hasDecision = true)
        {
            var syncEnv = CreatePlayerBaselineEnvelope(sessionId, roomId, seatId, stateVersion, eventSeq, hasDecision);
            var playerSnap = syncEnv.SyncResponse.Player;
            playerSnap.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq };

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PlayerUpdate,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = seatId,
                BindingEpoch = 1,
                CorrelationId = string.Empty,
                PlayerUpdate = new PlayerUpdate
                {
                    Snapshot = playerSnap,
                    FromCursor = isFullSnapshot ? null : new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq - 1 },
                    Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq },
                    IsFullSnapshot = isFullSnapshot
                }
            };
        }

        // =========================================================================
        // Formalized Review Probes (ProbeReview01 - ProbeReview11)
        // =========================================================================

        [UnityTest]
        public IEnumerator ProbeReview01_SentWithoutReceipt_MustKeepCanonicalPendingAndPreventResubmit()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 25; i++) yield return null;
            int before = GetUnresolvedOutboxCount();
            _presenter.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(1, before);

            Assert.AreEqual(1, GetUnresolvedOutboxCount(), "same decision must keep original request id until receipt");
            Assert.IsNotNull(_presenter.InteractionState.Pending, "transport Sent is not a receipt");
        }

        [UnityTest]
        public IEnumerator ProbeReview02_NextRoundDoubleClick_MustSubmitOnce()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.PublicView.Board.Phase = GamePhase.RoundEnd;
            snap.PublicView.SettlementRoundId = "round_1";
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap, true));
            var next = _presenter.GetComponentsInChildren<Button>(true);
            foreach (var b in next) if (b.name == "NextRoundButton") { b.onClick.Invoke(); b.onClick.Invoke(); }
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(1, GetUnresolvedOutboxCount(), "next-round buttons must use pending gate");
        }

        [UnityTest]
        public IEnumerator ProbeReview03_StaleSnapshot_MustNotReplaceDisplayedConfirmedState()
        {
            yield return LoadAndBindTableSceneAsync();
            var newer = CreatePlayerBaselineEnvelope(stateVersion: 10).SyncResponse.Player;
            newer.Cursor.ProjectionSeq = 10;
            newer.PublicView.Board.WallRemaining = 42;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(newer));
            _presenter.OnSessionViewChanged(this, FormalProbeView(CreatePlayerBaselineEnvelope().SyncResponse.Player));
            Assert.AreEqual(10UL, _presenter.LatestView!.Identity.StateVersion, "rejected view must not render");
            Assert.AreEqual(42u, _presenter.LatestView.Board.WallRemaining);
        }

        [UnityTest]
        public IEnumerator ProbeReview04_PrivateAuthorizationLost_MustCloseAndScrubPrivateDialog()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "private_offer",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 124,
                SecondTileId = 132,
                HasDeadline = true,
                DeadlineRemainingMs = 10000
            };
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenSleeveBloomDialog();
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap, false, TableConnectionState.Closed));
            Assert.IsFalse(_presenter.LatestView!.Identity.IsPrivateAuthorized);
            var dialog = _presenter.transform.Find("SleeveBloomDialog");
            Assert.IsNotNull(dialog);
            Assert.IsFalse(dialog!.gameObject.activeInHierarchy, "private sleeve cards still visible after auth loss");
        }

        [UnityTest]
        public IEnumerator ProbeReview05_ClearAll_MustClearHandAndSeatPrivateText()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.ClearAll();
            foreach (var b in _presenter.GetComponentsInChildren<Button>())
                Assert.IsFalse(b.name.StartsWith("HandTile_"), "old private hand buttons still visible after clear");
        }

        [UnityTest]
        public IEnumerator ProbeReview06_MultipleChiCandidates_MustAllowChoiceBeforeSubmitting()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Decision.Actions.Clear();
            snap.PrivateView.Decision.Actions.Add(new LegalActionView { ActionType = 5, OptionId = "chi_A", TileIds = { 0, 4 } });
            snap.PrivateView.Decision.Actions.Add(new LegalActionView { ActionType = 5, OptionId = "chi_B", TileIds = { 4, 8 } });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            foreach (var b in _presenter.GetComponentsInChildren<Button>(true)) if (b.name == "ChiButton") b.onClick.Invoke();
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "must present both chi candidates for explicit selection");
        }

        [UnityTest]
        public IEnumerator ProbeReview07_BaselineReset_MustCancelCurrentlyDisplayedPrivateAnimation()
        {
            yield return LoadAndBindTableSceneAsync();
            var next = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            next.Cursor.ProjectionSeq = 2;
            next.PrivateView.DrawnTileId = 128;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(next));
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var field = typeof(TablePresenter).GetField("_animationBannerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var text = (TextMeshProUGUI)field.GetValue(_presenter);
            Assert.IsTrue(text.gameObject.activeSelf, "probe requires active confirmed animation");
            _presenter.OnSessionViewChanged(this, FormalProbeView(next, true));
            Assert.IsFalse(text.gameObject.activeSelf, "old displayed animation must stop on baseline");
        }

        [UnityTest]
        public IEnumerator ProbeReview08_RealPointerTileMarkConfirmation_MustSubmit()
        {
            yield return LoadAndBindTableSceneAsync();
            UiInputLifecycle.ResetForTest();
            _presenter!.OpenTileMarkDialog();
            for (int i = 0; i < 4; i++) _presenter.OnHandTileClicked(i);
            var button = Array.Find(_presenter.GetComponentsInChildren<Button>(true), b => b.name == "ConfirmTileMarkButton");
            Assert.IsNotNull(button);
            var evt = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                position = new Vector2(640, 360)
            };
            ExecuteEvents.Execute(button!.gameObject, evt, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, evt, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject, evt, ExecuteEvents.pointerClickHandler);
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(1, GetUnresolvedOutboxCount(), "real pointer confirmation must reach client outbox");
        }

        [UnityTest]
        public IEnumerator ProbeReview09_PausedPointerDiscard_MustNotSubmit()
        {
            yield return LoadAndBindTableSceneAsync();
            UiInputLifecycle.ResetForTest();
            _presenter!.OnHandTileClicked(0);
            UiInputLifecycle.Instance.OnPause();
            var button = Array.Find(_presenter.GetComponentsInChildren<Button>(true), b => b.name == "DiscardButton");
            var evt = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                pointerId = -1
            };
            ExecuteEvents.Execute(button!.gameObject, evt, ExecuteEvents.pointerClickHandler);
            for (int i = 0; i < 25; i++) yield return null;
            UiInputLifecycle.Instance.OnResume();
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "paused input must be rejected by table buttons");
        }

        [UnityTest]
        public IEnumerator ProbeReview10_SinkGenerationMismatch_MustRejectOldViewCommand()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.Bind(new ProbeGenerationSink(_coordinator!), _mockNav, _coordinator);
            _presenter.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "old view generation must not submit to new sink generation");
        }

        private sealed class ProbeGenerationSink : ITableCommandSink
        {
            private readonly ITableCommandSink _sink;
            public ProbeGenerationSink(ITableCommandSink sink) { _sink = sink; }
            public bool CanSubmit => _sink.CanSubmit;
            public long Generation => _sink.Generation + 1;
            public Task<CommandSubmission> SubmitAsync(ProtocolCommand c, CancellationToken t) => _sink.SubmitAsync(c, t);
            public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved
            {
                add => _sink.ReceiptResolved += value;
                remove => _sink.ReceiptResolved -= value;
            }
        }

        [UnityTest]
        public IEnumerator ProbeReview11_SleeveOfferChanged_MustNotSubmitChoiceFromOldDialog()
        {
            yield return LoadAndBindTableSceneAsync();
            UiInputLifecycle.ResetForTest();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "offer_old",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 124,
                SecondTileId = 132,
                HasDeadline = true,
                DeadlineRemainingMs = 10000
            };
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenSleeveBloomDialog();
            snap.StateVersion = 3; snap.Cursor.ProjectionSeq = 3;
            snap.PrivateView.Cheat.SleeveBloom.OfferId = "offer_new";
            snap.PrivateView.Cheat.SleeveBloom.FirstTileId = 108;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            var button = Array.Find(_presenter.GetComponentsInChildren<Button>(true), b => b.name == "SleeveStoreButton");
            Assert.IsNotNull(button);
            if (button!.gameObject.activeInHierarchy) button.onClick.Invoke();
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "old offer dialog must close before new offer can be explicitly selected");
        }

        // =========================================================================
        // Official End-to-End Steps and Screenshot Baselines (Step01 - Step10)
        // =========================================================================

        [UnityTest]
        public IEnumerator Step01_RealBaseline_FourSeats_DiscardsMelds_Dora_AndCaptureScreenshot()
        {
            yield return LoadAndBindTableSceneAsync();

            Assert.IsNotNull(_presenter!.LatestView, "PlayerTableViewState must be projected");
            Assert.IsTrue(_presenter.LatestView!.Identity.IsPrivateAuthorized, "Private must be authorized");
            Assert.AreEqual(0, _presenter.LatestView.Identity.Seat);
            Assert.AreEqual(68u, _presenter.LatestView.Board.WallRemaining);

            CaptureScreen("01_table_four_seats_baseline.png");
        }

        [UnityTest]
        public IEnumerator Step02_RealClick_CandidateInteraction_And_OutboxSubmission()
        {
            yield return LoadAndBindTableSceneAsync();

            _presenter!.OnHandTileClicked(0);
            yield return null;

            Assert.IsTrue(_presenter.InteractionState.Selection.HasSelection, "Hand tile selection active");

            int outboxBefore = GetUnresolvedOutboxCount();
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == outboxBefore; i++)
            {
                yield return null;
            }

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore + 1, outboxAfter, "Canonical outbox command must be persisted upon submission");

            CaptureScreen("02_candidate_interaction_tsumo_discard.png");
        }

        [UnityTest]
        public IEnumerator Step03_RepeatedClicks_And_StaleDecision_Rejection()
        {
            yield return LoadAndBindTableSceneAsync();

            _presenter!.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == 0; i++)
            {
                yield return null;
            }

            Assert.IsNotNull(_presenter.InteractionState.Pending, "Pending intent must be active");
            int outboxBefore = GetUnresolvedOutboxCount();
            Assert.AreEqual(1, outboxBefore, "First command must be persisted in outbox");

            _presenter.OnDiscardButtonClicked();
            yield return null;

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore, outboxAfter, "Repeated click must not create second outbox entry (AlreadyPending)");

            var staleCandidate = new PlayerLegalActionState(99, PlayerActionKind.DeclareChi, 5, "stale_option", Array.Empty<uint>(), "round_stale", "decision_stale", 1UL);
            var staleOutcome = _presenter.InteractionState.SelectAction(staleCandidate);
            Assert.IsFalse(staleOutcome.Accepted, "Stale action candidate must be rejected");
        }

        [UnityTest]
        public IEnumerator Step04_Cheat_TileMark_PrimaryAndSecondarySelection()
        {
            yield return LoadAndBindTableSceneAsync();

            _presenter!.OpenTileMarkDialog();

            _presenter.OnHandTileClicked(0);
            _presenter.OnHandTileClicked(1);
            _presenter.OnHandTileClicked(2);
            _presenter.OnHandTileClicked(3);

            CaptureScreen("03_cheat_tilemark_primary_secondary.png");

            int outboxBefore = GetUnresolvedOutboxCount();
            _presenter.OnConfirmTileMarkClicked();
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == outboxBefore; i++)
            {
                yield return null;
            }

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore + 1, outboxAfter, "TileMark canonical command must be persisted to outbox");
        }

        [UnityTest]
        public IEnumerator Step05_Cheat_SleeveBloom_OfferChoice()
        {
            yield return LoadAndBindTableSceneAsync();

            var offerEnv = CreatePlayerPushEnvelope("session_table_1", "room_table_1", 0, 2, 102, 2, false, true);
            offerEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "sleeve_offer_1",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 10000
            };

            _channel!.EnqueueEnvelope(offerEnv);
            var pump = _session!.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            for (int i = 0; i < 10; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            _presenter!.OpenSleeveBloomDialog();

            CaptureScreen("04_cheat_sleeve_bloom_choice.png");

            int outboxBefore = GetUnresolvedOutboxCount();
            _presenter.OnSleeveChoiceClicked("SWAP");
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == outboxBefore; i++)
            {
                yield return null;
            }

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore + 1, outboxAfter, "Sleeve choice must be persisted to outbox");
        }

        [UnityTest]
        public IEnumerator Step06_Cheat_BeamSwap_SlotSelection()
        {
            yield return LoadAndBindTableSceneAsync();

            var beamEnv = CreatePlayerPushEnvelope("session_table_1", "room_table_1", 0, 3, 103, 2, false, true);
            beamEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap = new BeamSwapView
            {
                OfferId = "beam_offer_1",
                HasDeadline = true,
                DeadlineRemainingMs = 10000
            };
            beamEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            beamEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView
            {
                Slot = 0,
                TileId = 36,
                SlotKind = BeamSwapSlotKind.Wall,
                SlotIndex = 0
            });
            beamEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView
            {
                Slot = 1,
                TileId = 40,
                SlotKind = BeamSwapSlotKind.DeadWall,
                SlotIndex = 1
            });

            _channel!.EnqueueEnvelope(beamEnv);
            var pump = _session!.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            for (int i = 0; i < 10; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            _presenter!.OpenBeamSwapDialog();

            CaptureScreen("05_cheat_beam_swap_slots.png");

            int outboxBefore = GetUnresolvedOutboxCount();
            _presenter.OnKeepBeamOriginalClicked();
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == outboxBefore; i++)
            {
                yield return null;
            }

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore + 1, outboxAfter, "Beam keep original choice must be persisted to outbox");
        }

        [UnityTest]
        public IEnumerator Step07_StackingPreparation_AndCountdown()
        {
            yield return LoadAndBindTableSceneAsync();

            _presenter!.OpenPreparationDialog();

            CaptureScreen("06_stacking_preparation.png");

            _presenter.OnCancelPreparationClicked();
            _presenter.OnHandTileClicked(6);
            yield return null;

            CaptureScreen("07_countdown_and_control.png");
        }

        [UnityTest]
        public IEnumerator Step08_Reconnect_BaselineReset_And_SettlementNavigation()
        {
            yield return LoadAndBindTableSceneAsync();

            var resetEnv = CreatePlayerPushEnvelope("session_table_1", "room_table_1", 0, 4, 104, 2, isFullSnapshot: true, hasDecision: true);
            resetEnv.PlayerUpdate.Snapshot.PublicView.SettlementRoundId = string.Empty;

            _channel!.EnqueueEnvelope(resetEnv);
            var pump = _session!.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            for (int i = 0; i < 10; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            Assert.IsFalse(_presenter!.InteractionState.Selection.HasSelection);

            CaptureScreen("08_reconnect_baseline_reset.png");

            var settleEnv = CreatePlayerPushEnvelope("session_table_1", "room_table_1", 0, 5, 105, 3, isFullSnapshot: true, hasDecision: false);
            settleEnv.PlayerUpdate.Snapshot.PublicView.SettlementRoundId = "round_1";
            settleEnv.PlayerUpdate.Snapshot.PublicView.Board.Phase = GamePhase.RoundEnd;

            _channel.EnqueueEnvelope(settleEnv);
            pump = _session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            for (int i = 0; i < 10; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            CaptureScreen("09_settlement_navigation.png");

            _presenter.OnViewSettlementClicked();
            Assert.IsTrue(_mockNav!.SettlementOpened, "Settlement navigation callback must be invoked");
        }

        [UnityTest]
        public IEnumerator Step09_MultiAspect_Ultrawide21x9_TextBoundsFit()
        {
            yield return LoadAndBindTableSceneAsync();

            TestScreenshotHelper.AssertAllActiveTextCornersAndHeightsFitViewports(_canvas!);

            CaptureScreen("10_table_21x9_ultrawide.png", 2560, 1080);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Step10_NegativeMatrix_UnknownRedraw_GenerationClosed_PrivateAuthSwitch_TimeoutInput()
        {
            yield return LoadAndBindTableSceneAsync();

            _presenter!.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 30 && _presenter.InteractionState.Pending == null; i++)
            {
                yield return null;
            }

            string pendingReqId = _presenter.InteractionState.Pending.Intent.Command.RequestId.Value;

            // Inject real Wire receipt with Unknown / NeedsReconciliation through SessionViewState
            var unknownSnapshot = new PendingCommandSnapshot(
                new IdempotencyKey("user_seat_0", new RequestScopeId("session_table_1:seat:0"), new RequestId(pendingReqId)),
                CommandKind.SubmitAction,
                digestSha256: "test_digest",
                payloadLength: 16,
                handedToTransport: true,
                deliveryUnknown: true,
                needsReconciliation: true,
                lastLookup: ReceiptLookupStatus.Unknown,
                lastReceipt: ReceiptStatus.Unknown,
                queryInFlight: false);

            var cur = _coordinator!.Current;
            var binding = PlayModeScriptedTableChannel.CreateBinding("session_table_1", "room_table_1", ProtocolRole.Player, 0);
            var unknownView = new SessionViewState(
                cur.Generation,
                TableConnectionState.Ready,
                binding,
                cur.PlayerSnapshot,
                cur.PublicSnapshot,
                cur.RoomSnapshot,
                new[] { unknownSnapshot },
                null);

            _presenter.OnSessionViewChanged(this, unknownView);
            Assert.IsTrue(_presenter.InteractionState.Pending!.NeedsReconciliation, "Reconciled Unknown receipt must set NeedsReconciliation");
            Assert.AreEqual(pendingReqId, _presenter.InteractionState.Pending.Intent.Command.RequestId.Value, "Pending request ID must be preserved");

            yield return null;
            var shutTask = _coordinator.ShutdownAsync("TEST_SHUTDOWN");
            while (!shutTask.IsCompleted) yield return null;

            Assert.IsFalse(_coordinator.CanSubmit, "CanSubmit must be false after shutdown");
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await _coordinator.SubmitAsync(TestCommand(), CancellationToken.None);
            });

            var specEnv = PlayModeScriptedTableChannel.CreatePublicSync("session_table_1", "room_table_1", 1, 68);
            var specView = new SessionViewState(
                2,
                TableConnectionState.Ready,
                PlayModeScriptedTableChannel.CreateBinding("session_table_1", "room_table_1", ProtocolRole.Spectator, -1),
                null,
                specEnv.SyncResponse.Public,
                null,
                null,
                null);

            _presenter.OnSessionViewChanged(this, specView);
            Assert.IsFalse(_presenter.LatestView!.Identity.IsPrivateAuthorized, "Must not be private authorized");
            Assert.IsNull(_presenter.LatestView.Own, "Own state must be null for spectator");

            _presenter.AnimationQueue.AcceptConfirmedBaseline(_presenter.LatestView);
            Assert.DoesNotThrow(() =>
            {
                _presenter.OnCancelSelectionClicked();
            });
        }

        private T FormalField<T>(string name) => (T)typeof(TablePresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_presenter);

        private Button FormalButton(string name) => Array.Find(_presenter!.GetComponentsInChildren<Button>(true), b => b.name == name)!;

        private void FormalClick(Button b)
        {
            var e = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, pointerId = -1, position = new Vector2(640, 360) };
            ExecuteEvents.Execute(b.gameObject, e, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(b.gameObject, e, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(b.gameObject, e, ExecuteEvents.pointerClickHandler);
        }

        [UnityTest]
        public IEnumerator R2FormalProbe01_RealAppliedReceipt_MustReleasePresenterPending()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.OnHandTileClicked(0); _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(1, GetUnresolvedOutboxCount());
            Envelope? sent = null;
            foreach (var env in _channel!.SentEnvelopes) if (env.MessageType == MessageType.Command) sent = env;
            Assert.IsNotNull(sent);
            var receipt = sent!.Clone(); receipt.Command = null; receipt.MessageType = MessageType.Receipt;
            receipt.Receipt = new Mahjong.Contracts.Wire.Receipt { Status = Mahjong.Contracts.Wire.ReceiptStatus.Applied, CommandVersion = 2, EventSeq = 102, EvaluatedStateVersion = 1 };
            var decoded = WireCodec.Decode(WireCodec.Encode(receipt));
            Assert.IsTrue(decoded.IsOk, "receipt decoding: " + decoded.Status + " / " + decoded.ErrorCode);
            _channel.EnqueueEnvelope(receipt);
            var pump = _session!.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            for (int i = 0; i < 25; i++) { _dispatcher!.Update(); yield return null; }
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "probe requires Client accepted receipt; state=" + _session.State + " error=" + _session.LastError);
            Assert.IsNull(_presenter.InteractionState.Pending, "real Client ReceiptResolved must reach UI");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe02_StaleFullBaseline_MustRespectModelRejection()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.OnSessionViewChanged(this, FormalProbeView(CreatePlayerBaselineEnvelope(stateVersion: 10).SyncResponse.Player));
            _presenter.OnSessionViewChanged(this, FormalProbeView(CreatePlayerBaselineEnvelope(stateVersion: 1).SyncResponse.Player, true));
            Assert.AreEqual(10UL, _presenter.LatestView!.Identity.StateVersion, "baseline does not authorize state rollback");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe03_OlderGeneration_MustNotReplaceNewIdentity()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            _presenter!.OnSessionViewChanged(this, new SessionViewState(2, TableConnectionState.Ready, _session!.Binding, snap, null, null, null, null, true));
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap, true));
            Assert.AreEqual(2, _presenter.LatestView!.Identity.Generation);
        }

        [UnityTest]
        public IEnumerator R2FormalProbe04_AuthorizationLost_MustCancelVisiblePrivateAnimation()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player; snap.Cursor.ProjectionSeq = 2; snap.PrivateView.DrawnTileId = 128;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf);
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap, false, TableConnectionState.Closed));
            Assert.IsFalse(banner.gameObject.activeInHierarchy, "private confirmed tile still visible after authorization loss");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe05_ClearAll_MustScrubInactivePrivateText()
        {
            yield return LoadAndBindTableSceneAsync();
            var texts = FormalField<TextMeshProUGUI[]>("_handTileTexts"); Assert.IsNotEmpty(texts[0].text);
            _presenter!.ClearAll();
            Assert.IsEmpty(texts[0].text, "inactive hand objects must not retain private text");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe06_PausedCandidatePointer_MustNotSubmit()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Decision.Actions.Clear();
            snap.PrivateView.Decision.Actions.Add(new LegalActionView { ActionType = 5, OptionId = "chi_A", TileIds = { 0, 4 } });
            snap.PrivateView.Decision.Actions.Add(new LegalActionView { ActionType = 5, OptionId = "chi_B", TileIds = { 4, 8 } });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap)); _presenter.OnActionButtonClicked(PlayerActionKind.DeclareChi);
            UiInputLifecycle.Instance.OnPause();
            FormalClick(FormalButton("CandidateOption_1"));
            for (int i = 0; i < 25; i++) yield return null;
            UiInputLifecycle.Instance.OnResume();
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "candidate selection must obey pause gate");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe07_SevenBeamCandidates_MustAllBeReachable()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Cheat.BeamSwap = new BeamSwapView { OfferId = "beam_seven", HasDeadline = true, DeadlineRemainingMs = 10000 };
            snap.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            for (uint i = 0; i < 7; i++) snap.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView { Slot = i, SlotKind = BeamSwapSlotKind.Wall, SlotIndex = i, TileId = 36 + i });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap)); _presenter.OpenBeamSwapDialog();
            int reachable = 0; foreach (var b in _presenter.GetComponentsInChildren<Button>()) if (b.name.StartsWith("BeamSlot_")) reachable++;
            Assert.GreaterOrEqual(reachable, 7, "all server candidates must be reachable or paginated");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe08_PauseResume_MustInvalidateOldDiscardSelection()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.OnHandTileClicked(0); Assert.IsTrue(_presenter.InteractionState.Selection.HasSelection);
            _presenter.SendMessage("OnApplicationPause", true, SendMessageOptions.DontRequireReceiver);
            UiInputLifecycle.Instance.OnPause(); yield return null;
            _presenter.SendMessage("OnApplicationPause", false, SendMessageOptions.DontRequireReceiver);
            UiInputLifecycle.Instance.OnResume();
            Assert.IsFalse(_presenter.InteractionState.Selection.HasSelection, "old selection must expire across pause/resume");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe09_BeamOfferChanged_MustRequireExplicitReselection()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Cheat.BeamSwap = new BeamSwapView { OfferId = "beam_old", HasDeadline = true, DeadlineRemainingMs = 10000 };
            snap.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            snap.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView { Slot = 0, SlotKind = BeamSwapSlotKind.Wall, SlotIndex = 0, TileId = 36 });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap)); _presenter.OpenBeamSwapDialog();
            _presenter.OnHandTileClicked(0); _presenter.OnBeamSlotClicked(0);
            snap.StateVersion = 3; snap.Cursor.ProjectionSeq = 3; snap.PrivateView.Cheat.BeamSwap.OfferId = "beam_new";
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            var confirm = FormalButton("ConfirmBeamSwapButton"); if (confirm.gameObject.activeInHierarchy) FormalClick(confirm);
            for (int i = 0; i < 25; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "old beam choice must not target new offer");
        }

        [UnityTest]
        public IEnumerator R2FormalProbe10_AvailableCheatActivation_MustHaveReachableButtons()
        {
            yield return LoadAndBindTableSceneAsync();
            var factory = new TableCommandIntentFactory(new SystemTableClientClock());
            var view = _presenter!.LatestView!;
            var owner = new TableCommandOwnerContext(new RequestId("activation_probe"), new RequestScopeId(view.Identity.ScopeId), new StateVersion(view.Identity.StateVersion), new OwnerEpoch(view.Identity.OwnerEpoch));
            Assert.DoesNotThrow(() => factory.CreateSubmitSleeveBloom(view, owner));
            Assert.DoesNotThrow(() => factory.CreateSubmitBeamSwap(view, owner));
            bool sleeve = FormalButton("SleeveBloomButton").gameObject.activeInHierarchy;
            bool beam = FormalButton("BeamSwapButton").gameObject.activeInHierarchy;
            Assert.IsTrue(sleeve && beam, "factory-supported activations must be reachable before offers; sleeve=" + sleeve + " beam=" + beam);
        }

        [UnityTest]
        public IEnumerator R3FormalProbe01_AuthorizationLossMustScrubInactivePrivateText()
        {
            yield return LoadAndBindTableSceneAsync();
            var texts = FormalField<TextMeshProUGUI[]>("_handTileTexts");
            Assert.IsNotEmpty(texts[0].text);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap, false, TableConnectionState.Closed));
            Assert.IsNull(_presenter.LatestView!.Own);
            Assert.IsFalse(texts[0].gameObject.activeInHierarchy);
            Assert.IsEmpty(texts[0].text, "revoked private tile retained in inactive object");
        }

        [UnityTest]
        public IEnumerator R3FormalProbe02_UnbindMustClearVisiblePrivateState()
        {
            yield return LoadAndBindTableSceneAsync();
            var panel = FormalField<GameObject>("_ownHandPanelRoot");
            Assert.IsTrue(panel.activeInHierarchy);
            _presenter!.Unbind();
            Assert.IsTrue(!panel.activeInHierarchy || string.IsNullOrEmpty(FormalField<TextMeshProUGUI[]>("_handTileTexts")[0].text), "Unbind left old private hand visible");
        }

        [UnityTest]
        public IEnumerator R3FormalProbe03_NinthBeamCandidateMustHaveDistinctClickablePosition()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Cheat.BeamSwap = new BeamSwapView { OfferId = "beam_nine", HasDeadline = true, DeadlineRemainingMs = 10000 };
            snap.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            for (uint i = 0; i < 9; i++) snap.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView { Slot = i, SlotKind = BeamSwapSlotKind.Wall, SlotIndex = i, TileId = 36 + i });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenBeamSwapDialog();
            Canvas.ForceUpdateCanvases();
            var first = (RectTransform)FormalButton("BeamSlot_0").transform;
            var last = (RectTransform)FormalButton("BeamSlot_8").transform;
            CaptureScreen("r3_nine_beam_candidates.png");
            Assert.Greater(Vector3.Distance(first.position, last.position), 1f, "slot 8 overlays slot 0: first=" + first.position + " last=" + last.position);
        }

        [UnityTest]
        public IEnumerator R3FormalProbe04_SeventhActionCandidateMustHaveDistinctClickablePosition()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.PrivateView.Decision.Actions.Clear();
            for (int i = 0; i < 7; i++) snap.PrivateView.Decision.Actions.Add(new LegalActionView { ActionType = 5, OptionId = "chi_" + i, TileIds = { 0, 4 } });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OnActionButtonClicked(PlayerActionKind.DeclareChi);
            Canvas.ForceUpdateCanvases();
            var first = (RectTransform)FormalButton("CandidateOption_0").transform;
            var last = (RectTransform)FormalButton("CandidateOption_6").transform;
            CaptureScreen("r3_seven_action_candidates.png");
            Assert.Greater(Vector3.Distance(first.position, last.position), 1f, "option 6 overlays option 0: first=" + first.position + " last=" + last.position);
        }

        [UnityTest]
        public IEnumerator R3FormalProbe05_ControlEpochChangeMustInvalidateLocalTileMarkSelection()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.OpenTileMarkDialog();
            for (int i = 0; i < 4; i++) _presenter.OnHandTileClicked(i);
            Assert.AreEqual(4, FormalField<List<uint>>("_tileMarkSelectedTiles").Count);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Decision.ControlEpoch = 2;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            var confirm = FormalButton("ConfirmTileMarkButton");
            if (confirm.gameObject.activeInHierarchy) FormalClick(confirm);
            for (int i = 0; i < 30; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "old local tile-mark selection silently retargeted new control epoch");
        }

        [UnityTest]
        public IEnumerator R3FormalProbe06_AnimationFenceRejectionMustNotCommitDisplay()
        {
            yield return LoadAndBindTableSceneAsync();
            Assert.AreEqual(101UL, _presenter!.LatestView!.Identity.EventSeq);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, eventSeq: 100).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.DrawnTileId = 128;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.AreEqual(101UL, _presenter.LatestView!.Identity.EventSeq, "animation queue rejected EventSeqRegression but presenter committed regressed snapshot: rejection=" + _presenter.AnimationQueue.LastRejection);
        }

        [UnityTest]
        public IEnumerator R3FormalProbe07_OffTurnWindowMustUseOffTurnActivationCommand()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PublicView.Board.CurrentSeat = 1;
            snap.PrivateView.Cheat.WindowId = "window_offturn_confirmed";
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            var view = _presenter.LatestView!;
            var owner = new TableCommandOwnerContext(new RequestId("offturn_probe"), new RequestScopeId(view.Identity.ScopeId), new StateVersion(view.Identity.StateVersion), new OwnerEpoch(view.Identity.OwnerEpoch));
            Assert.DoesNotThrow(() => new TableCommandIntentFactory(new SystemTableClientClock()).CreateSubmitSleeveBloomOffTurn(view, owner));
            FormalClick(FormalButton("SleeveBloomButton"));
            for (int i = 0; i < 30; i++) yield return null;
            Assert.AreEqual(1, GetUnresolvedOutboxCount());
            Assert.AreEqual(CommandKind.SubmitSleeveBloomOffTurn, _presenter.InteractionState.Pending!.Intent.Kind, "confirmed off-turn window sent immediate activation instead");
        }

        [UnityTest]
        public IEnumerator R4FormalProbe01_RejectedRoundBaselineMustNotPoisonAnimationContext()
        {
            yield return LoadAndBindTableSceneAsync();
            var rejected = CreatePlayerBaselineEnvelope().SyncResponse.Player;
            rejected.RoundId = "round_invalid_same_version";
            _presenter!.OnSessionViewChanged(this, FormalProbeView(rejected, true));
            Assert.AreEqual("round_1", _presenter.LatestView!.Identity.RoundId);
            var valid = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            valid.Cursor.ProjectionSeq = 2;
            _presenter.OnSessionViewChanged(this, FormalProbeView(valid));
            Assert.AreEqual(2UL, _presenter.LatestView!.Identity.StateVersion, "valid update frozen after model rejection; queue=" + _presenter.AnimationQueue.LastRejection);
        }

        [UnityTest]
        public IEnumerator R4FormalProbe02_AuthorizationRestoreWithoutBaselineMustResume()
        {
            yield return LoadAndBindTableSceneAsync();
            var revoked = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            revoked.Cursor.ProjectionSeq = 2;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(revoked, false, TableConnectionState.Closed));
            Assert.IsNull(_presenter.LatestView!.Own);
            var restored = CreatePlayerBaselineEnvelope(stateVersion: 3).SyncResponse.Player;
            restored.Cursor.ProjectionSeq = 3;
            _presenter.OnSessionViewChanged(this, FormalProbeView(restored));
            Assert.IsNotNull(_presenter.LatestView!.Own, "authorized snapshot rejected after loss; queue=" + _presenter.AnimationQueue.LastRejection);
            Assert.AreEqual(3UL, _presenter.LatestView.Identity.StateVersion);
            restored.StateVersion = 4;
            restored.Cursor.ProjectionSeq = 4;
            _presenter.OnSessionViewChanged(this, FormalProbeView(restored));
            Assert.AreEqual(4UL, _presenter.LatestView.Identity.StateVersion);
        }

        [UnityTest]
        public IEnumerator R4FormalProbe03_AuthorizationRestoreWithBaselineMustResume()
        {
            yield return LoadAndBindTableSceneAsync();
            var revoked = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            revoked.Cursor.ProjectionSeq = 2;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(revoked, false, TableConnectionState.Closed));
            var restored = CreatePlayerBaselineEnvelope(stateVersion: 3).SyncResponse.Player;
            restored.Cursor.ProjectionSeq = 3;
            _presenter.OnSessionViewChanged(this, FormalProbeView(restored, true));
            Assert.IsNotNull(_presenter.LatestView!.Own);
            restored.StateVersion = 4;
            restored.Cursor.ProjectionSeq = 4;
            _presenter.OnSessionViewChanged(this, FormalProbeView(restored));
            Assert.AreEqual(4UL, _presenter.LatestView!.Identity.StateVersion);
        }

        [UnityTest]
        public IEnumerator R4FormalProbe04_UnchangedNoDecisionOfferMustKeepDialogOpenAndSubmittable()
        {
            yield return LoadAndBindTableSceneAsync(false);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "stable_offer",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 10000
            };
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenSleeveBloomDialog();
            var dialog = FormalField<GameObject>("_sleeveBloomDialog");
            Assert.IsTrue(dialog.activeInHierarchy, "offer must open");

            // 1. Duplicate snapshot: dialog remains open
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(dialog.activeInHierarchy, "unchanged confirmed offer was closed by duplicate snapshot");

            // 2. Cursor update: dialog remains open
            snap.Cursor.ProjectionSeq = 3;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(dialog.activeInHierarchy, "unchanged confirmed offer was closed by stream cursor advance");

            // 3. Public state update in same round: dialog remains open
            snap.Cursor.ProjectionSeq = 4;
            snap.PublicView.Board.WallRemaining = 60;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(dialog.activeInHierarchy, "unchanged confirmed offer was closed by public board update");

            // 4. Verify actual click submits to canonical outbox
            FormalClick(FormalButton("SleeveStoreButton"));
            for (int i = 0; i < 30; i++) yield return null;
            Assert.AreEqual(1, GetUnresolvedOutboxCount(), "clicking choice button must emit to outbox even without normal decision");
            Assert.AreEqual(CommandKind.SubmitSleeveBloomChoice, _presenter.InteractionState.Pending!.Intent.Kind);

            // 5. Negative matrix: offer changed closes dialog
            snap.StateVersion = 3;
            snap.Cursor.ProjectionSeq = 5;
            snap.PrivateView.Cheat.SleeveBloom.OfferId = "new_offer";
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsFalse(dialog.activeInHierarchy, "dialog must close when offer id changes");

            // 6. Reopen with new offer, then verify authorization loss closes dialog
            _presenter.OpenSleeveBloomDialog();
            Assert.IsTrue(dialog.activeInHierarchy);
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap, false, TableConnectionState.Closed));
            Assert.IsFalse(dialog.activeInHierarchy, "dialog must close when authorization is lost");
        }

        [UnityTest]
        public IEnumerator R4FormalProbe05_RebindToCurrentNonBaselineMustResume()
        {
            yield return LoadAndBindTableSceneAsync();
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            snap.StateVersion = 3;
            snap.Cursor.ProjectionSeq = 3;
            _presenter.Bind(_coordinator, _mockNav, new FormalSessionSnapshotView(FormalProbeView(snap)));
            Assert.AreEqual(3UL, _presenter.LatestView!.Identity.StateVersion);
            snap.StateVersion = 4;
            snap.Cursor.ProjectionSeq = 4;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.AreEqual(4UL, _presenter.LatestView!.Identity.StateVersion, "rebind froze confirmation; queue=" + _presenter.AnimationQueue.LastRejection);
            snap.StateVersion = 5;
            snap.Cursor.ProjectionSeq = 5;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.AreEqual(5UL, _presenter.LatestView!.Identity.StateVersion, "subsequent diff after rebind frozen; queue=" + _presenter.AnimationQueue.LastRejection);
        }

        [UnityTest]
        public IEnumerator R5FormalProbe01_ExpiredSleeveOfferMustCloseOnDuplicateRedraw()
        {
            yield return LoadAndBindTableSceneAsync(false);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "expiry_sleeve_01",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 300
            };
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenSleeveBloomDialog();
            var dialog = FormalField<GameObject>("_sleeveBloomDialog");
            Assert.IsTrue(dialog.activeInHierarchy);
            yield return new WaitForSecondsRealtime(0.5f);
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(_presenter.LatestView!.Own!.Cheat!.ImmediateSleeveBloom!.Countdown!.IsExpired(FormalField<ITableClientClock>("_clientClock").MonotonicMilliseconds), "precondition: model deadline expired");
            Assert.IsFalse(dialog.activeInHierarchy, "expired sleeve dialog survives duplicate confirmed snapshot");

            // Negative assertion: clicking choice on expired offer must not submit command to outbox
            _presenter.OnSleeveChoiceClicked(SleeveBloomChoice.First);
            for (int i = 0; i < 20; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "expired sleeve choice must not submit intent to outbox");
        }

        [UnityTest]
        public IEnumerator R5FormalProbe02_ExpiredBeamOfferMustCloseOnDuplicateRedraw()
        {
            yield return LoadAndBindTableSceneAsync(false);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Cheat.BeamSwap = new BeamSwapView
            {
                OfferId = "expiry_beam_01",
                HasDeadline = true,
                DeadlineRemainingMs = 300
            };
            snap.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            snap.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView { Slot = 0, SlotKind = BeamSwapSlotKind.Wall, SlotIndex = 0, TileId = 36 });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenBeamSwapDialog();
            var dialog = FormalField<GameObject>("_beamSwapDialog");
            Assert.IsTrue(dialog.activeInHierarchy);
            yield return new WaitForSecondsRealtime(0.5f);
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(_presenter.LatestView!.Own!.Cheat!.BeamSwap!.Countdown!.IsExpired(FormalField<ITableClientClock>("_clientClock").MonotonicMilliseconds), "precondition: model deadline expired");
            Assert.IsFalse(dialog.activeInHierarchy, "expired beam dialog survives duplicate confirmed snapshot");

            // Negative assertion: clicking confirm or keep original on expired offer must not submit command to outbox
            _presenter.OnHandTileClicked(0);
            _presenter.OnBeamSlotClicked(0);
            _presenter.OnConfirmBeamSwapClicked();
            _presenter.OnKeepBeamOriginalClicked();
            for (int i = 0; i < 20; i++) yield return null;
            Assert.AreEqual(0, GetUnresolvedOutboxCount(), "expired beam choice must not submit intent to outbox");
        }

        [UnityTest]
        public IEnumerator R5FormalProbe03_MonotonicTimePassageMustCloseExpiredOffersWithoutNewSnapshots()
        {
            yield return LoadAndBindTableSceneAsync(false);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "expiry_time_passage_sleeve",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 300
            };
            snap.PrivateView.Cheat.BeamSwap = new BeamSwapView
            {
                OfferId = "expiry_time_passage_beam",
                HasDeadline = true,
                DeadlineRemainingMs = 300
            };
            snap.PrivateView.Cheat.BeamSwap.SelectableHandTileIds.Add(0);
            snap.PrivateView.Cheat.BeamSwap.Candidates.Add(new BeamSwapCandidateView { Slot = 0, SlotKind = BeamSwapSlotKind.Wall, SlotIndex = 0, TileId = 36 });
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));

            // Open sleeve dialog and wait for natural expiration via Update()
            _presenter.OpenSleeveBloomDialog();
            var sleeveDialog = FormalField<GameObject>("_sleeveBloomDialog");
            Assert.IsTrue(sleeveDialog.activeInHierarchy);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(sleeveDialog.activeInHierarchy, "sleeve dialog must auto-close on monotonic deadline expiration via Update()");

            // Open beam dialog with fresh offer
            snap.StateVersion = 3;
            snap.Cursor.ProjectionSeq = 3;
            snap.PrivateView.Cheat.BeamSwap.OfferId = "expiry_time_passage_beam_2";
            snap.PrivateView.Cheat.BeamSwap.DeadlineRemainingMs = 300;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenBeamSwapDialog();
            _presenter.OnHandTileClicked(0);
            _presenter.OnBeamSlotClicked(0);
            var beamDialog = FormalField<GameObject>("_beamSwapDialog");
            Assert.IsTrue(beamDialog.activeInHierarchy);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(beamDialog.activeInHierarchy, "beam dialog must auto-close on monotonic deadline expiration via Update()");
            Assert.IsNull(FormalField<uint?>("_selectedBeamHandTile"), "beam hand tile selection must be cleared upon expiration");
            Assert.IsNull(FormalField<PlayerBeamSwapCandidateState?>("_selectedBeamCandidate"), "beam candidate selection must be cleared upon expiration");
        }

        [UnityTest]
        public IEnumerator R5FormalProbe04_UnexpiredCheatOffersMustRemainOpenAcrossPublicDiffAndCursorAdvance()
        {
            yield return LoadAndBindTableSceneAsync(false);
            var snap = CreatePlayerBaselineEnvelope(stateVersion: 2, hasDecision: false).SyncResponse.Player;
            snap.Cursor.ProjectionSeq = 2;
            snap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "long_lived_sleeve",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 15000
            };
            _presenter!.OnSessionViewChanged(this, FormalProbeView(snap));
            _presenter.OpenSleeveBloomDialog();
            var sleeveDialog = FormalField<GameObject>("_sleeveBloomDialog");
            Assert.IsTrue(sleeveDialog.activeInHierarchy);

            // Advance cursor and public board state
            snap.StateVersion = 3;
            snap.Cursor.ProjectionSeq = 3;
            snap.PublicView.Board.WallRemaining = 65;
            _presenter.OnSessionViewChanged(this, FormalProbeView(snap));
            Assert.IsTrue(sleeveDialog.activeInHierarchy, "unexpired sleeve dialog must remain open across non-decision public updates");

            // Negative: expired offer cannot be re-opened
            var expiredSnap = CreatePlayerBaselineEnvelope(stateVersion: 4, hasDecision: false).SyncResponse.Player;
            expiredSnap.Cursor.ProjectionSeq = 4;
            expiredSnap.PrivateView.Cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "already_expired_sleeve",
                Mode = "IMMEDIATE",
                DrawKind = SleeveBloomDrawKind.Normal,
                FirstTileId = 0,
                SecondTileId = 4,
                HasDeadline = true,
                DeadlineRemainingMs = 0
            };
            _presenter.OnSessionViewChanged(this, FormalProbeView(expiredSnap));
            _presenter.OpenSleeveBloomDialog();
            Assert.IsFalse(sleeveDialog.activeInHierarchy, "expired offer with 0 remaining ms must not open");
        }

        private static EventEnvelope B2bEvent(
            ulong eventSeq,
            uint ordinal,
            WireEventType type,
            EventVisibility visibility,
            ulong commandVersion,
            Action<EventEnvelope> body)
        {
            var envelope = new EventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = type,
                Visibility = visibility
            };
            body(envelope);
            return envelope;
        }

        private static Envelope B2bTypedPush(
            PlayerSnapshot snapshot,
            ulong fromProjection,
            ulong toProjection,
            bool isFullSnapshot,
            params EventEnvelope[] events)
        {
            var update = new PlayerUpdate
            {
                Snapshot = snapshot,
                FromCursor = isFullSnapshot
                    ? null
                    : new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = fromProjection },
                Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = toProjection },
                IsFullSnapshot = isFullSnapshot
            };
            for (int i = 0; i < events.Length; i++)
            {
                update.VisibleEvents.Add(events[i]);
            }

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PlayerUpdate,
                SessionId = "session_table_1",
                RoomId = "room_table_1",
                SeatId = 0,
                BindingEpoch = 1,
                PlayerUpdate = update
            };
        }

        private IEnumerator PumpAndDispatch()
        {
            var pump = _session!.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.IsFalse(pump.IsFaulted, pump.Exception?.ToString());
            for (int i = 0; i < 40; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }
        }

        private IEnumerator SendTypedDiscard(
            ulong stateVersion,
            ulong eventSeq,
            ulong fromProjection,
            ulong toProjection,
            uint tileId)
        {
            PlayerSnapshot snapshot = _session!.PlayerSnapshot!.Clone();
            snapshot.StateVersion = stateVersion;
            snapshot.EventSeq = eventSeq;
            snapshot.Cursor = new Mahjong.Contracts.Wire.StreamCursor
            {
                StreamVersion = 1,
                ProjectionSeq = toProjection
            };
            snapshot.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = tileId,
                TurnNumber = (uint)stateVersion,
                Tsumogiri = false
            });

            Envelope push = B2bTypedPush(
                snapshot,
                fromProjection,
                toProjection,
                false,
                B2bEvent(eventSeq, 0, WireEventType.TileDiscarded, EventVisibility.Public, stateVersion,
                    e => e.TileDiscarded = new TileDiscarded
                    {
                        TurnNumber = (uint)stateVersion,
                        Player = 1,
                        TileId = tileId
                    }));
            _channel!.EnqueueEnvelope(push);
            yield return PumpAndDispatch();
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe01_RealTypedBatchAnimatesOnceAndSuppressesSnapshotDouble()
        {
            yield return LoadAndBindTableSceneAsync();
            Assert.IsNotNull(_presenter);
            Assert.IsNotNull(_session);
            // Keep the queue deterministic: the normal MonoBehaviour Update
            // would dequeue one animation before the assertion.
            _presenter!.enabled = false;

            PlayerSnapshot snapshot = CreatePlayerPushEnvelope(stateVersion: 2, eventSeq: 106, projectionSeq: 2).PlayerUpdate.Snapshot;
            snapshot.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 2,
                Tsumogiri = false
            });
            snapshot.PrivateView.DrawnTileId = 27;

            Envelope push = B2bTypedPush(
                snapshot,
                1,
                2,
                false,
                B2bEvent(106, 0, WireEventType.TileDiscarded, EventVisibility.Public, 2,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 2, Player = 1, TileId = 44 }),
                B2bEvent(106, 1, WireEventType.TileDrawn, EventVisibility.Private, 2,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 27, WallRemaining = 67 }));
            _channel!.EnqueueEnvelope(push);
            yield return PumpAndDispatch();

            Assert.IsNotNull(_presenter!.LastVisibleEventMapping, "real typed batch must reach the presenter");
            Assert.True(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(2, _presenter.LastVisibleEventMapping.AnimatedEvents.Count);
            Assert.AreEqual(2, _presenter.AnimationQueue.PendingCount,
                "the same snapshot discard/draw must not be replayed by the snapshot difference; rejected=" +
                _presenter.AnimationQueue.LastRejection + "; events=" + string.Join(",", System.Linq.Enumerable.Select(_presenter.LastVisibleEventMapping.AnimatedEvents, e => e.Kind + "@" + e.EventSeq + "/" + e.EventOrdinal)));

            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf);
            StringAssert.Contains("1", banner.text);
            Assert.IsNotEmpty(banner.text, "the renderer banner must show the confirmed animation");

            var remaining = _presenter.AnimationQueue.Drain();
            Assert.AreEqual(1, remaining.Count);
            Assert.AreEqual(TableAnimationKind.Draw, remaining[0].Kind);
            Assert.AreEqual(27U, remaining[0].TileId);
            CaptureScreen("11_b2b_typed_event_animation.png");
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe02_FullBaselineEventsNeverAnimateAndResetClearsQueue()
        {
            yield return LoadAndBindTableSceneAsync();
            PlayerSnapshot snapshot = CreatePlayerPushEnvelope(stateVersion: 2, eventSeq: 106, projectionSeq: 2, isFullSnapshot: true).PlayerUpdate.Snapshot;
            snapshot.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 2,
                Tsumogiri = false
            });
            Envelope baselinePush = B2bTypedPush(
                snapshot,
                0,
                2,
                true,
                B2bEvent(106, 0, WireEventType.TileDiscarded, EventVisibility.Public, 2,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 2, Player = 1, TileId = 44 }));
            _channel!.EnqueueEnvelope(baselinePush);
            yield return PumpAndDispatch();

            Assert.IsNull(_presenter!.LastVisibleEventMapping, "baseline events are reconciliation only");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "baseline event must not be replayed as animation");
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsFalse(banner.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe03_HistoricalEmptyIncrementalMustNotUseSnapshotFallback()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            // A full baseline establishes event watermark 120.
            PlayerSnapshot baseline = CreatePlayerPushEnvelope(
                stateVersion: 22,
                eventSeq: 120,
                projectionSeq: 2,
                isFullSnapshot: true).PlayerUpdate.Snapshot;
            _channel!.EnqueueEnvelope(B2bTypedPush(baseline, 0, 2, true));
            yield return PumpAndDispatch();
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);

            // The confirmed snapshot advances, but its event at/below the
            // watermark is filtered by the real Client (Events=0,
            // PlayableEvents=0). It still must not be replayed by the snapshot
            // difference fallback.
            PlayerSnapshot staleIncrement = _session!.PlayerSnapshot!.Clone();
            staleIncrement.StateVersion = 23;
            staleIncrement.EventSeq = 120;
            staleIncrement.Cursor = new Mahjong.Contracts.Wire.StreamCursor
            {
                StreamVersion = 1,
                ProjectionSeq = 3
            };
            staleIncrement.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 23,
                Tsumogiri = false
            });
            _channel.EnqueueEnvelope(B2bTypedPush(
                staleIncrement,
                2,
                3,
                false,
                B2bEvent(120, 0, WireEventType.TileDiscarded, EventVisibility.Public, 23,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 23, Player = 1, TileId = 44 })));
            yield return PumpAndDispatch();

            Assert.AreEqual(23UL, _presenter.LatestView!.Identity.StateVersion,
                "the accepted empty-playable incremental must still advance the confirmed view");
            Assert.IsNotNull(_presenter.LastVisibleEventMapping,
                "a real incremental batch must use the typed path even with zero playable events");
            Assert.True(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(0, _presenter.LastVisibleEventMapping.AnimatedEvents.Count);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount,
                "historical filtered event must not animate through snapshot difference; rejected=" +
                _presenter.AnimationQueue.LastRejection);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsFalse(banner.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe04_PauseMustHideQueuedAndDisplayedAnimationAndFenceLateCallbacks()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            yield return SendTypedDiscard(2, 102, 1, 2, 44);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf);

            yield return SendTypedDiscard(3, 103, 2, 3, 45);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount, "second confirmed action is queued while the first banner shows");

            _presenter.SendMessage("OnApplicationPause", true, SendMessageOptions.DontRequireReceiver);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "pause must clear pending confirmed animation");
            Assert.IsFalse(banner.gameObject.activeInHierarchy, "pause must hide the displayed animation banner immediately");

            // A completed late callback while paused must not update the
            // presenter or repopulate the queue.
            yield return SendTypedDiscard(4, 104, 3, 4, 46);
            Assert.AreEqual(3UL, _presenter.LatestView!.Identity.StateVersion, "paused presenter must not consume late confirmed views");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            Assert.IsFalse(banner.gameObject.activeInHierarchy);

            _presenter.SendMessage("OnApplicationPause", false, SendMessageOptions.DontRequireReceiver);
            Assert.AreEqual(4UL, _presenter.LatestView!.Identity.StateVersion, "resume must re-render the current accepted state");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "resume must not replay the pre-pause batch");
            Assert.IsNull(_presenter.LastVisibleEventMapping);
            Assert.IsFalse(banner.gameObject.activeInHierarchy);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(banner.gameObject.activeSelf);

            // A genuinely new action after resume plays exactly once.
            yield return SendTypedDiscard(5, 105, 4, 5, 47);
            Assert.IsTrue(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsTrue(banner.gameObject.activeSelf);
            Assert.IsNotEmpty(banner.text);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe05_FocusLossMustHideQueuedAndDisplayedAnimationAndFenceLateCallbacks()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            yield return SendTypedDiscard(2, 102, 1, 2, 44);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf);

            yield return SendTypedDiscard(3, 103, 2, 3, 45);
            _presenter.SendMessage("OnApplicationFocus", false, SendMessageOptions.DontRequireReceiver);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            Assert.IsFalse(banner.gameObject.activeInHierarchy);

            yield return SendTypedDiscard(4, 104, 3, 4, 46);
            Assert.AreEqual(3UL, _presenter.LatestView!.Identity.StateVersion);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            Assert.IsFalse(banner.gameObject.activeInHierarchy);

            _presenter.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            Assert.AreEqual(4UL, _presenter.LatestView!.Identity.StateVersion);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            Assert.IsNull(_presenter.LastVisibleEventMapping);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(banner.gameObject.activeSelf);

            yield return SendTypedDiscard(5, 105, 4, 5, 47);
            Assert.IsTrue(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsTrue(banner.gameObject.activeSelf);
        }

        private IEnumerator RunPendingCaptureLifecycleScenario(
            bool pause,
            bool focus,
            bool releaseBeforeBoundary,
            bool rebindWhileSuspended)
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int captures = 0;
            var delayedCoordinator = new SessionCoordinator(
                _session,
                _queue,
                _channel,
                _dispatcher!,
                new AppConfig(),
                1,
                async () =>
                {
                    int call = Interlocked.Increment(ref captures);
                    if (call == 1)
                    {
                        await gate.Task;
                    }

                    return await _queue!.CaptureAsync(CancellationToken.None);
                });
            _coordinator = delayedCoordinator;
            _presenter.Bind(delayedCoordinator, _mockNav, delayedCoordinator, () => _mockNav!.OpenSettlement());

            PlayerSnapshot oldSnapshot = _session!.PlayerSnapshot!.Clone();
            oldSnapshot.StateVersion = 2;
            oldSnapshot.EventSeq = 102;
            oldSnapshot.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            oldSnapshot.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 2,
                Tsumogiri = false
            });
            _channel!.EnqueueEnvelope(B2bTypedPush(
                oldSnapshot,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDiscarded, EventVisibility.Public, 2,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 2, Player = 1, TileId = 44 })));
            var oldPump = _session.PumpOnceAsync(CancellationToken.None);
            while (!oldPump.IsCompleted) yield return null;
            for (int i = 0; i < 30 && Volatile.Read(ref captures) == 0; i++)
            {
                yield return null;
            }

            Assert.GreaterOrEqual(Volatile.Read(ref captures), 1);
            Assert.IsNull(_coordinator.Current.PlayerSnapshot,
                "the old event is accepted but its capture is still pending");

            if (releaseBeforeBoundary)
            {
                gate.SetResult(true);
                for (int i = 0; i < 50; i++)
                {
                    PlayerSnapshot? current = _coordinator.Current.PlayerSnapshot;
                    if (current != null && current.StateVersion >= 2)
                    {
                        break;
                    }

                    yield return null;
                }

                Assert.IsNotNull(_coordinator.Current.PlayerSnapshot);
                Assert.AreEqual(2UL, _coordinator.Current.PlayerSnapshot!.StateVersion,
                    "capture has completed and published, but dispatcher has not delivered it");
            }

            if (pause)
            {
                _presenter.SendMessage("OnApplicationPause", true, SendMessageOptions.DontRequireReceiver);
            }

            if (focus)
            {
                _presenter.SendMessage("OnApplicationFocus", false, SendMessageOptions.DontRequireReceiver);
            }

            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "lifecycle boundary must clear pending animation");
            Assert.IsFalse(banner.gameObject.activeInHierarchy, "lifecycle boundary must hide the animation banner");

            if (rebindWhileSuspended)
            {
                _presenter.Unbind();
                _presenter.Bind(delayedCoordinator, _mockNav, delayedCoordinator, () => _mockNav!.OpenSettlement());
            }

            if (pause)
            {
                _presenter.SendMessage("OnApplicationPause", false, SendMessageOptions.DontRequireReceiver);
            }

            if (focus)
            {
                _presenter.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            }

            if (!releaseBeforeBoundary)
            {
                gate.SetResult(true);
            }

            for (int i = 0; i < 60; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            Assert.AreEqual(2UL, _presenter.LatestView!.Identity.StateVersion,
                "the stale publication may advance state but may not be dropped or rolled back");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount,
                "the pre-lifecycle capture must not enqueue an old animation");
            Assert.IsNull(_presenter.LastVisibleEventMapping);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(banner.gameObject.activeSelf);

            yield return SendTypedDiscard(3, 103, 2, 3, 45);
            Assert.IsTrue(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount,
                "a genuinely new post-resume action must still play exactly once");
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe07_PendingCaptureAcrossPauseMustNotReviveAnimation()
        {
            yield return RunPendingCaptureLifecycleScenario(pause: true, focus: false, releaseBeforeBoundary: false, rebindWhileSuspended: false);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe08_PendingCaptureAcrossFocusLossMustNotReviveAnimation()
        {
            yield return RunPendingCaptureLifecycleScenario(pause: false, focus: true, releaseBeforeBoundary: false, rebindWhileSuspended: false);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe09_PendingCaptureAcrossPauseAndFocusMustNotReviveAnimation()
        {
            yield return RunPendingCaptureLifecycleScenario(pause: true, focus: true, releaseBeforeBoundary: false, rebindWhileSuspended: false);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe10_PublishedUndispatchedCaptureAcrossPauseMustNotReviveAnimation()
        {
            yield return RunPendingCaptureLifecycleScenario(pause: true, focus: false, releaseBeforeBoundary: true, rebindWhileSuspended: false);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe11_PendingCaptureAcrossUnbindRebindMustNotReviveAnimation()
        {
            yield return RunPendingCaptureLifecycleScenario(pause: true, focus: false, releaseBeforeBoundary: false, rebindWhileSuspended: true);
        }

        private IEnumerator ReconnectBinding(
            string roomId,
            Role role,
            int seat,
            int bindingEpoch,
            ulong connectionEpoch,
            ulong ownerEpoch,
            PlayerSnapshot? player)
        {
            var disconnect = _session!.DisconnectAsync(CancellationToken.None);
            while (!disconnect.IsCompleted) yield return null;
            var reopen = _session.OpenAsync(
                new TableSessionOpenRequest("ticket_1", "client_1"),
                CancellationToken.None);
            while (!reopen.IsCompleted) yield return null;

            Envelope welcome = PlayModeScriptedTableChannel.CreateWelcome(
                "session_table_1",
                roomId,
                role,
                seat,
                bindingEpoch);
            welcome.Welcome.ConnectionEpoch = connectionEpoch;
            welcome.Welcome.OwnerEpoch = ownerEpoch;
            welcome.Welcome.Scope.RoomId = roomId;
            _channel!.EnqueueEnvelope(welcome);
            var welcomePump = _session.PumpOnceAsync(CancellationToken.None);
            while (!welcomePump.IsCompleted) yield return null;

            string? correlation = _channel.SentEnvelopes[_channel.SentEnvelopes.Count - 1].CorrelationId;
            Envelope sync;
            if (role == Role.Player)
            {
                sync = CreatePlayerBaselineEnvelope(
                    sessionId: "session_table_1",
                    roomId: roomId,
                    seatId: seat,
                    stateVersion: 5,
                    eventSeq: 105,
                    correlationId: correlation);
                sync.BindingEpoch = (ulong)bindingEpoch;
                if (player != null)
                {
                    sync.SyncResponse.Player = player.Clone();
                }
            }
            else
            {
                sync = PlayModeScriptedTableChannel.CreatePublicSync(
                    "session_table_1",
                    roomId,
                    bindingEpoch,
                    68,
                    correlation);
                sync.SyncResponse.StateVersion = 2;
                sync.SyncResponse.EventSeq = 102;
                sync.SyncResponse.Public.StateVersion = 2;
                sync.SyncResponse.Public.EventSeq = 102;
                sync.SyncResponse.Public.Cursor = new Mahjong.Contracts.Wire.StreamCursor
                {
                    StreamVersion = 1,
                    ProjectionSeq = 2
                };
            }

            _channel.EnqueueEnvelope(sync);
            var syncPump = _session.PumpOnceAsync(CancellationToken.None);
            while (!syncPump.IsCompleted) yield return null;
            for (int i = 0; i < 50; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe12_PrivateQueueThenSpectatorRebindMustClearRenderer()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            PlayerSnapshot draw = _session!.PlayerSnapshot!.Clone();
            draw.StateVersion = 2;
            draw.EventSeq = 102;
            draw.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            draw.PrivateView.DrawnTileId = 27;
            _channel!.EnqueueEnvelope(B2bTypedPush(
                draw,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDrawn, EventVisibility.Private, 2,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 27, WallRemaining = 67 })));
            yield return PumpAndDispatch();
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf, "private queue/banner must exist before the role boundary");

            yield return ReconnectBinding(
                "room_table_1",
                Role.Spectator,
                -1,
                2,
                2,
                2,
                null);

            Assert.AreEqual(TableConnectionState.Ready, _session.State, "spectator rebind session state");
            Assert.IsNotNull(_session.PublicSnapshot, "spectator rebind public baseline");
            Assert.AreEqual(Mahjong.Contracts.ProtocolRole.Spectator, _session.Binding!.Role, "real session role after spectator rebind");
            Assert.AreEqual(PlayerTableRole.Spectator, (PlayerTableRole)_coordinator.Current.Binding!.Role, "coordinator binding role after spectator rebind");
            Assert.AreEqual(PlayerTableRole.Spectator, _presenter.LatestView!.Identity.Role);
            Assert.IsFalse(_presenter.LatestView.Identity.IsPrivateAuthorized);
            Assert.IsNull(_presenter.LatestView.Own);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "role switch must clear the prior private queue");
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(banner.gameObject.activeSelf, "role switch must hide the prior private banner");
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe13_PrivateQueueThenPrivateRevokeMustClearRenderer()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            PlayerSnapshot draw = _session!.PlayerSnapshot!.Clone();
            draw.StateVersion = 2;
            draw.EventSeq = 102;
            draw.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            draw.PrivateView.DrawnTileId = 27;
            _channel!.EnqueueEnvelope(B2bTypedPush(
                draw,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDrawn, EventVisibility.Private, 2,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 27, WallRemaining = 67 })));
            yield return PumpAndDispatch();
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsTrue(banner.gameObject.activeSelf);

            // The frozen Wire contract rejects a player snapshot without a
            // private view (ValidatePrivatePlayerView). The minimal test
            // channel therefore models the actual authorization-loss boundary
            // as a closed confirmed state on the same binding; the real
            // session path is exercised by the spectator rebind and room/epoch
            // probes. The Wire rejection itself remains pinned below.
            Envelope invalidPrivate = CreatePlayerBaselineEnvelope(
                stateVersion: 5,
                eventSeq: 105);
            invalidPrivate.SyncResponse.Player.PrivateView = null;
            Assert.Throws<WireMalformedException>(() => WireCodec.Encode(invalidPrivate),
                "player private revocation is not representable on the frozen player wire");

            var revoked = new SessionViewState(
                1,
                TableConnectionState.Closed,
                _session!.Binding,
                _session.PlayerSnapshot!.Clone(),
                null,
                null,
                null,
                "PRIVATE_REVOKED",
                false,
                null);
            _presenter!.OnSessionViewChanged(this, revoked);

            Assert.IsFalse(_presenter.LatestView!.Identity.IsPrivateAuthorized,
                "private revocation must reach the presenter");
            Assert.IsNull(_presenter.LatestView.Own);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(banner.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe14_RoomConnectionOwnerRebindMustClearPrivateQueue()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            PlayerSnapshot draw = _session!.PlayerSnapshot!.Clone();
            draw.StateVersion = 2;
            draw.EventSeq = 102;
            draw.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            draw.PrivateView.DrawnTileId = 27;
            _channel!.EnqueueEnvelope(B2bTypedPush(
                draw,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDrawn, EventVisibility.Private, 2,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 27, WallRemaining = 67 })));
            yield return PumpAndDispatch();
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);

            PlayerSnapshot reboundPlayer = CreatePlayerBaselineEnvelope(
                sessionId: "session_table_1",
                roomId: "room_next",
                stateVersion: 5,
                eventSeq: 105).SyncResponse.Player;
            yield return ReconnectBinding(
                "room_next",
                Role.Player,
                0,
                3,
                7,
                9,
                reboundPlayer);

            Assert.AreEqual("room_next", _presenter.LatestView!.Identity.RoomId);
            Assert.AreEqual(3UL, _presenter.LatestView.Identity.BindingEpoch);
            Assert.AreEqual(7UL, _presenter.LatestView.Identity.ConnectionEpoch);
            Assert.AreEqual(9UL, _presenter.LatestView.Identity.OwnerEpoch);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount, "room/epoch rebind must clear the old private queue");
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            Assert.IsFalse(FormalField<TextMeshProUGUI>("_animationBannerText").gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe15_RoundResetMustClearPrivateQueueAndKeepNewAction()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            PlayerSnapshot draw = _session!.PlayerSnapshot!.Clone();
            draw.StateVersion = 2;
            draw.EventSeq = 102;
            draw.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            draw.PrivateView.DrawnTileId = 27;
            _channel!.EnqueueEnvelope(B2bTypedPush(
                draw,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDrawn, EventVisibility.Private, 2,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 27, WallRemaining = 67 })));
            yield return PumpAndDispatch();
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);

            PlayerSnapshot nextRound = CreatePlayerPushEnvelope(
                stateVersion: 3,
                eventSeq: 103,
                projectionSeq: 3,
                isFullSnapshot: true).PlayerUpdate.Snapshot;
            nextRound.RoundId = "round_next";
            nextRound.PublicView.Board.RoundNumber = 2;
            _channel.EnqueueEnvelope(B2bTypedPush(nextRound, 0, 3, true));
            yield return PumpAndDispatch();

            Assert.AreEqual("round_next", _presenter.LatestView!.Identity.RoundId);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsFalse(banner.gameObject.activeSelf);

            yield return SendTypedDiscard(4, 104, 3, 4, 45);
            Assert.IsTrue(_presenter.LastVisibleEventMapping!.Accepted, _presenter.LastVisibleEventMapping.Code);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe16_LegacyEmptyBatchKeepsDifferenceFallback()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            PlayerSnapshot next = _session!.PlayerSnapshot!.Clone();
            next.StateVersion = 3;
            next.EventSeq = 103;
            next.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 3 };
            next.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 3,
                Tsumogiri = false
            });

            var legacyView = new SessionViewState(
                1,
                TableConnectionState.Ready,
                _session.Binding,
                next,
                null,
                null,
                null,
                null,
                false,
                new StreamCursor(new StreamVersion(next.Cursor.StreamVersion), new ProjectionSeq(next.Cursor.ProjectionSeq)));
            _presenter.OnSessionViewChanged(this, legacyView);

            Assert.IsNull(_presenter.LastVisibleEventMapping);
            Assert.AreEqual(1, _presenter.AnimationQueue.PendingCount,
                "Kind=None legacy batch must retain the confirmed difference fallback");
            Assert.IsTrue(_presenter.AnimationQueue.TryDequeue(out TableAnimationItem? item));
            Assert.AreEqual(TableAnimationKind.Discard, item!.Kind);
        }

        [UnityTest]
        public IEnumerator B2bFormalProbe06_DelayedCaptureWithNewBindingMustNotRebaseOldBatch()
        {
            yield return LoadAndBindTableSceneAsync();
            _presenter!.enabled = false;

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int captures = 0;
            var delayedCoordinator = new SessionCoordinator(
                _session,
                _queue,
                _channel,
                _dispatcher!,
                new AppConfig(),
                1,
                async () =>
                {
                    int call = Interlocked.Increment(ref captures);
                    if (call == 1)
                    {
                        await gate.Task;
                    }

                    return await _queue!.CaptureAsync(CancellationToken.None);
                });
            _coordinator = delayedCoordinator;
            _presenter.Bind(delayedCoordinator, _mockNav, delayedCoordinator, () => _mockNav!.OpenSettlement());

            PlayerSnapshot oldSnapshot = _session!.PlayerSnapshot!.Clone();
            oldSnapshot.StateVersion = 2;
            oldSnapshot.EventSeq = 102;
            oldSnapshot.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 };
            oldSnapshot.PublicView.Board.Players[1].Discards.Add(new DiscardView
            {
                TileId = 44,
                TurnNumber = 2,
                Tsumogiri = false
            });
            _channel!.EnqueueEnvelope(B2bTypedPush(
                oldSnapshot,
                1,
                2,
                false,
                B2bEvent(102, 0, WireEventType.TileDiscarded, EventVisibility.Public, 2,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 2, Player = 1, TileId = 44 })));

            var oldPump = _session.PumpOnceAsync(CancellationToken.None);
            while (!oldPump.IsCompleted) yield return null;

            for (int i = 0; i < 30 && Volatile.Read(ref captures) == 0; i++)
            {
                yield return null;
            }

            Assert.GreaterOrEqual(Volatile.Read(ref captures), 1, "the old snapshot must be captured before the rebind");
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount,
                "old event must not be dispatched before the capture gate releases");

            // The old accepted snapshot is held inside CaptureAsync. A new
            // binding/room epoch arrives before that capture completes.
            var disconnect = _session.DisconnectAsync(CancellationToken.None);
            while (!disconnect.IsCompleted) yield return null;
            var reopen = _session.OpenAsync(
                new TableSessionOpenRequest("ticket_1", "client_1"),
                CancellationToken.None);
            while (!reopen.IsCompleted) yield return null;

            _channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome(
                "session_table_1",
                "room_table_1",
                Role.Player,
                0,
                bindingEpoch: 2));
            var welcomePump = _session.PumpOnceAsync(CancellationToken.None);
            while (!welcomePump.IsCompleted) yield return null;

            string? syncCorrelation = _channel.SentEnvelopes[_channel.SentEnvelopes.Count - 1].CorrelationId;
            Envelope reboundSync = CreatePlayerBaselineEnvelope(
                stateVersion: 5,
                eventSeq: 105,
                correlationId: syncCorrelation);
            reboundSync.BindingEpoch = 2;
            reboundSync.SyncResponse.Player.Cursor = new Mahjong.Contracts.Wire.StreamCursor
            {
                StreamVersion = 1,
                ProjectionSeq = 2
            };
            _channel.EnqueueEnvelope(reboundSync);
            var syncPump = _session.PumpOnceAsync(CancellationToken.None);
            while (!syncPump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, _session.State);
            Assert.AreEqual(2UL, _session.Binding!.BindingEpoch.Value);

            gate.SetResult(true);
            for (int i = 0; i < 50; i++)
            {
                _dispatcher!.Update();
                yield return null;
            }

            Assert.IsNotNull(_presenter.LatestView, "the new binding baseline must still reach the scene");
            Assert.AreEqual(2UL, _presenter.LatestView!.Identity.BindingEpoch);
            Assert.AreEqual(0, _presenter.AnimationQueue.PendingCount,
                "old typed batch must be dropped rather than rebased onto the new binding; rejected=" +
                _presenter.AnimationQueue.LastRejection);
            Assert.IsNull(_presenter.LastVisibleEventMapping);
            typeof(TablePresenter).GetMethod("UpdateAnimationBanner", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_presenter, null);
            var banner = FormalField<TextMeshProUGUI>("_animationBannerText");
            Assert.IsFalse(banner.gameObject.activeSelf);
        }

        private sealed class FormalSessionSnapshotView : ISessionView
        {
            public FormalSessionSnapshotView(SessionViewState current) { Current = current; }
            public SessionViewState Current { get; }
            public event EventHandler<SessionViewState>? Changed { add { } remove { } }
        }

        private static ProtocolCommand TestCommand()
        {
            var payload = CommandPayloadFactory.SubmitAction(
                "round_1",
                "dec_1",
                1UL,
                ProtocolActionType.DiscardTile,
                "opt_1",
                null);
            return new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("test_req"),
                new RequestScopeId("test_scope"),
                new StateVersion(1));
        }

        private int GetUnresolvedOutboxCount()
        {
            return _outbox!.LoadUnresolvedAsync(CancellationToken.None).GetAwaiter().GetResult().Count;
        }

        private sealed class MockNavigation : IAppNavigation
        {
            public bool LobbyOpened { get; private set; }
            public bool BackToMenuCalled { get; private set; }
            public bool QuitRequested { get; private set; }
            public bool SettlementOpened { get; private set; }

            public void OpenLobby() => LobbyOpened = true;
            public void BackToMenu() => BackToMenuCalled = true;
            public void RequestQuit() => QuitRequested = true;
            public void OpenSettlement() => SettlementOpened = true;
        }
    }
}
