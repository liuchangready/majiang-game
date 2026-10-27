#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
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

            // Dora indicators: 5万, 1筒
            snap.PublicView.Board.DoraIndicators.Add(16); // 5万
            snap.PublicView.Board.DoraIndicators.Add(72); // 1筒

            // 4 Players
            for (int i = 0; i < 4; i++)
            {
                snap.PublicView.Board.Players.Add(new PlayerPublicView
                {
                    Seat = i,
                    HandCount = 13,
                    Riichi = i == 2 // Seat 2 has declared Riichi
                });
                snap.PublicView.Board.Scores.Add(new ScoreView
                {
                    Seat = i,
                    Score = 25000
                });
            }

            // Seat 1 has discarded some tiles
            snap.PublicView.Board.Players[1].Discards.Add(new DiscardView { TileId = 36, Tsumogiri = true });
            // Seat 2 has discarded with riichi
            snap.PublicView.Board.Players[2].Discards.Add(new DiscardView { TileId = 40, RiichiDiscard = true });
            // Seat 3 has open meld (Chi) and opponent closed kan (0 tile IDs)
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

            // Player's private hand tiles: 13 closed tiles (0..12) + drawn tile 13 (4万)
            for (uint t = 0; t < 13; t++)
            {
                snap.PrivateView.ClosedTiles.Add(t * 2); // 0, 2, 4, 6...
            }
            snap.PrivateView.DrawnTileId = 26; // Drawn tile
            snap.PrivateView.SleeveTiles.Add(124); // 白 in sleeve

            // Decision
            if (hasDecision)
            {
                snap.PrivateView.Decision = new DecisionView
                {
                    DecisionId = "decision_live_1",
                    ControlEpoch = 1,
                    DeadlineRemainingMs = 18000
                };
                // Actions: DiscardTile (4), Tsumo (9), Riichi (8), Pass (11)
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

        [UnityTest]
        public IEnumerator Step01_RealBaseline_FourSeats_DiscardsMelds_Dora_AndCaptureScreenshot()
        {
            yield return LoadAndBindTableSceneAsync();

            // Assert baseline projection
            Assert.IsNotNull(_presenter!.LatestView, "PlayerTableViewState must be projected");
            Assert.IsTrue(_presenter.LatestView!.Identity.IsPrivateAuthorized, "Private must be authorized");
            Assert.AreEqual(0, _presenter.LatestView.Identity.Seat);
            Assert.AreEqual(68u, _presenter.LatestView.Board.WallRemaining);

            // Capture Screenshot 1: Four seats baseline
            CaptureScreen("01_table_four_seats_baseline.png");
        }

        [UnityTest]
        public IEnumerator Step02_RealClick_CandidateInteraction_And_OutboxSubmission()
        {
            yield return LoadAndBindTableSceneAsync();

            // Click on hand tile 0 (which matches discard candidate)
            _presenter!.OnHandTileClicked(0);
            yield return null;

            Assert.IsTrue(_presenter.InteractionState.Selection.HasSelection, "Hand tile selection active");

            int outboxBefore = GetUnresolvedOutboxCount();

            // Click Discard button
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 30 && GetUnresolvedOutboxCount() == outboxBefore; i++)
            {
                yield return null;
            }

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore + 1, outboxAfter, "Canonical outbox command must be persisted upon submission");

            // Capture Screenshot 2: Candidate interaction
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

            // While previous intent is pending, repeated click must return AlreadyPending
            _presenter.OnDiscardButtonClicked();
            yield return null;

            int outboxAfter = GetUnresolvedOutboxCount();
            Assert.AreEqual(outboxBefore, outboxAfter, "Repeated click must not create second outbox entry (AlreadyPending)");

            // Stale decision selection rejection test
            var staleCandidate = new PlayerLegalActionState(99, PlayerActionKind.DeclareChi, 5, "stale_option", Array.Empty<uint>(), "round_stale", "decision_stale", 1UL);
            var staleOutcome = _presenter.InteractionState.SelectAction(staleCandidate);
            Assert.IsFalse(staleOutcome.Accepted, "Stale action candidate must be rejected");
        }

        [UnityTest]
        public IEnumerator Step04_Cheat_TileMark_PrimaryAndSecondarySelection()
        {
            yield return LoadAndBindTableSceneAsync();

            // Open TileMark dialog
            _presenter!.OpenTileMarkDialog();

            // Select 4 tiles
            _presenter.OnHandTileClicked(0);
            _presenter.OnHandTileClicked(1);
            _presenter.OnHandTileClicked(2);
            _presenter.OnHandTileClicked(3);

            // Capture Screenshot 3: TileMark primary and secondary selection
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

            // Inject a push with active SleeveBloom offer
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

            // Capture Screenshot 4: SleeveBloom offer choices
            CaptureScreen("04_cheat_sleeve_bloom_choice.png");

            int outboxBefore = GetUnresolvedOutboxCount();
            _presenter.OnSleeveChoiceClicked(SleeveBloomChoice.First);
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

            // Inject a push with active BeamSwap offer and candidate slots
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

            // Capture Screenshot 5: BeamSwap slot selection
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

            // Capture Screenshot 6: Stacking Preparation
            CaptureScreen("06_stacking_preparation.png");

            // Close dialog to show table countdown & control view
            _presenter.OnCancelPreparationClicked();
            _presenter.OnHandTileClicked(6);
            yield return null;

            // Capture Screenshot 7: Countdown and Control
            CaptureScreen("07_countdown_and_control.png");
        }

        [UnityTest]
        public IEnumerator Step08_Reconnect_BaselineReset_And_SettlementNavigation()
        {
            yield return LoadAndBindTableSceneAsync();

            // 1. Server resets baseline (reconnect simulation)
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

            // Capture Screenshot 8: Reconnect baseline reset
            CaptureScreen("08_reconnect_baseline_reset.png");

            // 2. Round finished with settlement
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

            // Capture Screenshot 9: Settlement navigation entry
            CaptureScreen("09_settlement_navigation.png");

            _presenter.OnViewSettlementClicked();
            Assert.IsTrue(_mockNav!.SettlementOpened, "Settlement navigation callback must be invoked");
        }

        [UnityTest]
        public IEnumerator Step09_MultiAspect_Ultrawide21x9_TextBoundsFit()
        {
            yield return LoadAndBindTableSceneAsync();

            // Verify all active texts fit viewports and preferred height <= rect height
            TestScreenshotHelper.AssertAllActiveTextCornersAndHeightsFitViewports(_canvas!);

            // Capture Screenshot 10: 21:9 Ultrawide
            CaptureScreen("10_table_21x9_ultrawide.png", 2560, 1080);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Step10_NegativeMatrix_UnknownRedraw_GenerationClosed_PrivateAuthSwitch_TimeoutInput()
        {
            yield return LoadAndBindTableSceneAsync();

            // 1. First make an action selection so Pending is active
            _presenter!.OnHandTileClicked(0);
            _presenter.OnDiscardButtonClicked();
            for (int i = 0; i < 30 && _presenter.InteractionState.Pending == null; i++)
            {
                yield return null;
            }

            // Unknown delivery outcome test
            _presenter.InteractionState.MarkDeliveryUnknown();
            Assert.IsTrue(_presenter.InteractionState.Pending!.NeedsReconciliation, "Unknown must set NeedsReconciliation");

            // Repaint / re-applying view must NOT send new command or change RequestId
            string pendingReqId = _presenter.InteractionState.Pending.Intent.Command.RequestId.Value;
            _presenter.OnSessionViewChanged(this, _coordinator!.Current);
            Assert.AreEqual(pendingReqId, _presenter.InteractionState.Pending.Intent.Command.RequestId.Value);

            // 2. Generation closed / shutdown test
            yield return null;
            var shutTask = _coordinator.ShutdownAsync("TEST_SHUTDOWN");
            while (!shutTask.IsCompleted) yield return null;

            Assert.IsFalse(_coordinator.CanSubmit, "CanSubmit must be false after shutdown");
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await _coordinator.SubmitAsync(TestCommand(), CancellationToken.None);
            });

            // 3. Private authorization switch (e.g. switching to Spectator role)
            var specEnv = PlayModeScriptedTableChannel.CreatePublicSync("session_table_1", "room_table_1", 1, 68);
            var specView = new SessionViewState(
                2,
                TableConnectionState.Ready,
                PlayModeScriptedTableChannel.CreateBinding("session_table_1", "room_table_1", ProtocolRole.Spectator, -1),
                null, // No private snapshot
                specEnv.SyncResponse.Public,
                null,
                null,
                null);

            _presenter.OnSessionViewChanged(this, specView);
            Assert.IsFalse(_presenter.LatestView!.Identity.IsPrivateAuthorized, "Must not be private authorized");
            Assert.IsNull(_presenter.LatestView.Own, "Own state must be null for spectator");

            // 4. Timeout / animation continuation test: animation queue processing does not block user interaction
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
