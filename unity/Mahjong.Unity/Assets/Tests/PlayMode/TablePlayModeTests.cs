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
using ReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using ReceiptLookupStatus = Mahjong.Contracts.ReceiptLookupStatus;
using IdempotencyKey = Mahjong.Contracts.IdempotencyKey;

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
