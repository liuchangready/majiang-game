#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using Mahjong.Client;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;
using ProtocolErrorCode = Mahjong.Contracts.ProtocolErrorCode;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class SpectatorChannelPlayModeTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();

        private GameObject Go(string name)
        {
            var go = new GameObject(name);
            _owned.Add(go);
            return go;
        }

        private TextMeshProUGUI Text() => Go("testText").AddComponent<TextMeshProUGUI>();

        [SetUp]
        public void Setup() => UiThemeProvider.EnsureLineBreakingRules();

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _owned)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            _owned.Clear();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_SpectatorFlow_UpdatesSpectatorPresenter()
        {
            var channel = new PlayModeScriptedTableChannel();
            var outbox = new InMemoryOutboxStore();
            var session = new TableSession(channel, outbox);
            var queue = new TableSessionOperationQueue(session);
            var dispatcher = new MainThreadDispatcher();
            var coordinator = new SessionCoordinator(session, queue, channel, dispatcher, new AppConfig(), generation: 1);

            var presenterGo = Go("LiveSpectatorPresenter");
            var presenter = presenterGo.AddComponent<SpectatorPresenter>();
            var wallText = Text();
            var roundText = Text();
            presenter.Configure(wallCountText: wallText, roundStatusText: roundText);
            presenter.Rebind(coordinator);

            // Step 1: Open session
            var openTask = queue.OpenAsync(new TableSessionOpenRequest("ticket_1", "client_1"), CancellationToken.None);
            while (!channel.IsOpen) yield return null;

            // Step 2: Feed WELCOME frame
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome(
                sessionId: "live_session",
                roomId: "live_room",
                role: Role.Spectator,
                seatId: -1,
                bindingEpoch: 1));

            var pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session.State);

            // Step 3: Feed PublicSync frame (Full snapshot with wallRemaining = 68)
            var syncCorrelationId = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync(
                sessionId: "live_session",
                roomId: "live_room",
                bindingEpoch: 1,
                wallRemaining: 68,
                correlationId: syncCorrelationId));

            pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session.State);

            for (int i = 0; i < 30 && string.IsNullOrEmpty(wallText.text); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("68", wallText.text, "Real TableSession event must populate wall count in SpectatorPresenter");

            // Step 4: Feed PublicPush frame with wallRemaining = 54
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush(
                sessionId: "live_session",
                roomId: "live_room",
                bindingEpoch: 1,
                wallRemaining: 54));

            pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;

            for (int i = 0; i < 30 && !wallText.text.Contains("54"); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("54", wallText.text, "Real TableSession push must update SpectatorPresenter");

            channel.Dispose();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_SettlementFlow_UpdatesSettlementPresenter()
        {
            var channel = new PlayModeScriptedTableChannel();
            var outbox = new InMemoryOutboxStore();
            var session = new TableSession(channel, outbox);
            var queue = new TableSessionOperationQueue(session);
            var dispatcher = new MainThreadDispatcher();
            var coordinator = new SessionCoordinator(session, queue, channel, dispatcher, new AppConfig(), generation: 1);

            var presenterGo = Go("LiveSettlementPresenter");
            var presenter = presenterGo.AddComponent<SettlementPresenter>();
            var winSummary = Text();
            var paymentsText = Text();
            var rankingsText = Text();
            presenter.Configure(winSummaryText: winSummary, paymentsText: paymentsText, matchRankingsText: rankingsText);
            presenter.Rebind(coordinator);

            // Step 1: Open session
            var openTask = queue.OpenAsync(new TableSessionOpenRequest("ticket_1", "client_1"), CancellationToken.None);
            while (!channel.IsOpen) yield return null;

            // Step 2: Feed WELCOME frame
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome(
                sessionId: "live_session",
                roomId: "live_room",
                role: Role.Spectator,
                seatId: -1,
                bindingEpoch: 1));

            var pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;

            // Step 3: Feed PublicSync
            var syncCorrelationId = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync(
                sessionId: "live_session",
                roomId: "live_room",
                bindingEpoch: 1,
                wallRemaining: 68,
                correlationId: syncCorrelationId));

            pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;

            for (int i = 0; i < 30 && coordinator.Current.PublicSnapshot == null; i++)
            {
                dispatcher.Update();
                yield return null;
            }

            // Step 4: Feed Settlement push with RonWin and ScoreSettled
            var snap = new PublicSnapshot
            {
                SessionId = "live_session",
                RoundId = "round_1",
                StateVersion = 2,
                EventSeq = 102,
                Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 2 },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = 40,
                        Phase = GamePhase.RoundEnd,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0
                    },
                    Session = new PublicSessionSummary { Finished = true, FinishReason = "match_completed" },
                    SettlementRoundId = "round_1"
                }
            };
            for (int i = 0; i < 4; i++)
            {
                snap.View.Board.Players.Add(new PlayerPublicView { Seat = i, HandCount = 13 });
                snap.View.Board.Scores.Add(new ScoreView { Seat = i, Score = 25000 });
            }
            snap.View.Session.Rankings.AddRange(new int[] { 0, 1, 2, 3 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 0, Score = 35000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 1, Score = 25000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 2, Score = 22000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 3, Score = 18000 });

            var settled = new ScoreSettled();
            settled.Payments.Add(new PaymentView { FromSeat = 1, ToSeat = 0, Amount = 3900 });

            snap.View.SettlementEvents.Add(new PublicEventEnvelope
            {
                EventType = Mahjong.Contracts.Wire.EventType.RonWin,
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                Visibility = EventVisibility.SettlementPublic,
                RonWin = new RonWin { Winner = 0, Loser = 1, Score = 3900, Han = 2, Fu = 30 }
            });
            snap.View.SettlementEvents.Add(new PublicEventEnvelope
            {
                EventType = Mahjong.Contracts.Wire.EventType.ScoreSettled,
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                Visibility = EventVisibility.SettlementPublic,
                ScoreSettled = settled
            });

            var settlementPush = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PublicUpdate,
                SessionId = "live_session",
                RoomId = "live_room",
                SeatId = -1,
                BindingEpoch = 1,
                PublicUpdate = new PublicUpdate
                {
                    Snapshot = snap,
                    FromCursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                    Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 2 },
                    IsFullSnapshot = false
                }
            };

            channel.EnqueueEnvelope(settlementPush);
            pumpTask = session.PumpOnceAsync(CancellationToken.None);
            while (!pumpTask.IsCompleted) yield return null;

            for (int i = 0; i < 30 && !winSummary.text.Contains("3900"); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("3900", winSummary.text, "Real client event must populate win summary");
            StringAssert.Contains("1", paymentsText.text, "Real client event must show payer seat 1 in payment transfer");
            StringAssert.Contains("35000", rankingsText.text, "Real client event must show match rankings");

            channel.Dispose();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_SpectatorToPlayerToSpectator_RebindCleansState()
        {
            var presenterGo = Go("SettlementRebindPresenter");
            var presenter = presenterGo.AddComponent<SettlementPresenter>();
            var ledgerGo = Go("LedgerRoot");
            var ledgerPresenter = ledgerGo.AddComponent<PrivateLedgerPresenter>();
            var balanceText = Text();
            var entriesText = Text();
            var authText = Text();
            var localTag = Text();
            ledgerPresenter.Configure(ledgerGo, balanceText, entriesText, authText, localTag);
            presenter.Configure(privateLedger: ledgerPresenter);

            // Phase 1: Session 1 (Spectator)
            var channel1 = new PlayModeScriptedTableChannel();
            var session1 = new TableSession(channel1, new InMemoryOutboxStore());
            var queue1 = new TableSessionOperationQueue(session1);
            var dispatcher1 = new MainThreadDispatcher();
            var coord1 = new SessionCoordinator(session1, queue1, channel1, dispatcher1, new AppConfig(), generation: 1);
            presenter.Rebind(coord1);

            _ = queue1.OpenAsync(new TableSessionOpenRequest("t1", "c1"), CancellationToken.None);
            while (!channel1.IsOpen) yield return null;

            channel1.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_s1", "room_1", Role.Spectator, -1, 1));
            var pump = session1.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session1.State);

            var syncId1 = channel1.SentEnvelopes[channel1.SentEnvelopes.Count - 1].CorrelationId;
            channel1.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_s1", "room_1", 1, 68, syncId1));
            pump = session1.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session1.State);

            for (int i = 0; i < 50 && coord1.Current.PublicSnapshot == null; i++)
            {
                dispatcher1.Update();
                yield return null;
            }

            Assert.IsFalse(ledgerPresenter.IsVisible, "Spectator must NOT see private ledger in Session 1.");

            // Phase 2: Clean shutdown Session 1 via Coordinator barrier, Rebind to Session 2 (Player)
            var shutdownTask1 = coord1.ShutdownAsync("session1_ended");
            while (!shutdownTask1.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownTask1.Result.Outcome);
            Assert.IsFalse(shutdownTask1.Result.Quarantined);
            Assert.AreEqual(TableConnectionState.Closed, session1.State);
            channel1.Dispose();

            var channel2 = new PlayModeScriptedTableChannel();
            var session2 = new TableSession(channel2, new InMemoryOutboxStore());
            var queue2 = new TableSessionOperationQueue(session2);
            var dispatcher2 = new MainThreadDispatcher();
            var coord2 = new SessionCoordinator(session2, queue2, channel2, dispatcher2, new AppConfig(), generation: 2);
            presenter.Rebind(coord2);

            _ = queue2.OpenAsync(new TableSessionOpenRequest("t2", "c2"), CancellationToken.None);
            while (!channel2.IsOpen) yield return null;

            channel2.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_s2", "room_1", Role.Player, 0, 1));
            pump = session2.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session2.State);

            var syncId2 = channel2.SentEnvelopes[channel2.SentEnvelopes.Count - 1].CorrelationId;
            channel2.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePlayerSync("sess_s2", "room_1", 0, 1, 500, syncId2));
            pump = session2.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session2.State);

            for (int i = 0; i < 50 && !ledgerPresenter.IsVisible; i++)
            {
                dispatcher2.Update();
                yield return null;
            }

            Assert.IsTrue(ledgerPresenter.IsVisible, "Player must see authorized private ledger in Session 2.");
            StringAssert.Contains("500", balanceText.text);

            // Phase 3: Clean shutdown Session 2 via Coordinator barrier, Rebind to Session 3 (Spectator)
            var shutdownTask2 = coord2.ShutdownAsync("session2_ended");
            while (!shutdownTask2.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownTask2.Result.Outcome);
            Assert.IsFalse(shutdownTask2.Result.Quarantined);
            Assert.AreEqual(TableConnectionState.Closed, session2.State);
            channel2.Dispose();

            var channel3 = new PlayModeScriptedTableChannel();
            var session3 = new TableSession(channel3, new InMemoryOutboxStore());
            var queue3 = new TableSessionOperationQueue(session3);
            var dispatcher3 = new MainThreadDispatcher();
            var coord3 = new SessionCoordinator(session3, queue3, channel3, dispatcher3, new AppConfig(), generation: 3);
            presenter.Rebind(coord3);

            _ = queue3.OpenAsync(new TableSessionOpenRequest("t3", "c3"), CancellationToken.None);
            while (!channel3.IsOpen) yield return null;

            channel3.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_s3", "room_1", Role.Spectator, -1, 1));
            pump = session3.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session3.State);

            var syncId3 = channel3.SentEnvelopes[channel3.SentEnvelopes.Count - 1].CorrelationId;
            channel3.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_s3", "room_1", 1, 68, syncId3));
            pump = session3.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session3.State);

            for (int i = 0; i < 50 && coord3.Current.PublicSnapshot == null; i++)
            {
                dispatcher3.Update();
                yield return null;
            }

            Assert.IsFalse(ledgerPresenter.IsVisible, "Spectator in Session 3 must NOT retain any private ledger from Session 2.");
            Assert.IsEmpty(balanceText.text, "BalanceText must be cleared on rebind.");
            Assert.IsEmpty(entriesText.text, "EntriesText must be cleared on rebind.");

            var shutdownTask3 = coord3.ShutdownAsync("session3_ended");
            while (!shutdownTask3.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownTask3.Result.Outcome);
            Assert.IsFalse(shutdownTask3.Result.Quarantined);
            channel3.Dispose();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_ConnectionLoss_And_IncompatibleProtocolBanner()
        {
            // Part A: Connection Loss through real TableSession / Queue
            var channelA = new PlayModeScriptedTableChannel();
            var sessionA = new TableSession(channelA, new InMemoryOutboxStore());
            var queueA = new TableSessionOperationQueue(sessionA);
            var dispatcherA = new MainThreadDispatcher();
            var coordA = new SessionCoordinator(sessionA, queueA, channelA, dispatcherA, new AppConfig(), generation: 1);

            var bannerGo = Go("BannerRoot");
            var bannerPresenter = bannerGo.AddComponent<ConnectionBannerPresenter>();
            var bannerText = Text();
            var so = new UnityEditor.SerializedObject(bannerPresenter);
            so.FindProperty("_bannerRoot").objectReferenceValue = bannerGo;
            so.FindProperty("_bannerText").objectReferenceValue = bannerText;
            so.ApplyModifiedPropertiesWithoutUndo();

            var presGo = Go("SpectatorPres");
            var presenter = presGo.AddComponent<SpectatorPresenter>();
            presenter.Configure(connectionBanner: bannerPresenter);
            presenter.Rebind(coordA);

            _ = queueA.OpenAsync(new TableSessionOpenRequest("t1", "c1"), CancellationToken.None);
            while (!channelA.IsOpen) yield return null;

            channelA.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_a", "room_a", Role.Spectator, -1, 1));
            var pump = sessionA.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, sessionA.State);

            var syncId = channelA.SentEnvelopes[channelA.SentEnvelopes.Count - 1].CorrelationId;
            channelA.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_a", "room_a", 1, 68, syncId));
            pump = sessionA.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, sessionA.State);

            for (int i = 0; i < 50 && bannerText.text != "已连接"; i++)
            {
                dispatcherA.Update();
                yield return null;
            }

            Assert.AreEqual("已连接", bannerText.text);

            // Drive real close via session queue
            var closeTask = queueA.CloseAsync("network_disconnect", CancellationToken.None);
            while (!closeTask.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Closed, sessionA.State);

            coordA.Publish(1, new SessionViewState(
                1,
                sessionA.State,
                sessionA.Binding,
                null,
                coordA.Current.PublicSnapshot,
                null,
                Array.Empty<PendingCommandSnapshot>(),
                sessionA.LastError));

            for (int i = 0; i < 50 && bannerText.text != "连接已关闭"; i++)
            {
                dispatcherA.Update();
                yield return null;
            }

            Assert.AreEqual("连接已关闭", bannerText.text);

            var shutdownTaskA = coordA.ShutdownAsync("network_disconnect");
            while (!shutdownTaskA.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownTaskA.Result.Outcome);
            Assert.IsFalse(shutdownTaskA.Result.Quarantined);
            channelA.Dispose();

            // Part B: Incompatible Protocol through real TableSession / Queue / Channel
            var channelB = new PlayModeScriptedTableChannel();
            var sessionB = new TableSession(channelB, new InMemoryOutboxStore());
            var queueB = new TableSessionOperationQueue(sessionB);
            var dispatcherB = new MainThreadDispatcher();
            var coordB = new SessionCoordinator(sessionB, queueB, channelB, dispatcherB, new AppConfig(), generation: 2);

            var incompRoot = Go("IncompRoot");
            var incompText = Text();
            incompRoot.SetActive(false);

            presenter.Configure(incompatibleBannerRoot: incompRoot, incompatibleBannerText: incompText);
            presenter.Rebind(coordB);

            _ = queueB.OpenAsync(new TableSessionOpenRequest("t2", "c2"), CancellationToken.None);
            while (!channelB.IsOpen) yield return null;

            // Enqueue incompatible welcome (Major = frozen Major + 99)
            channelB.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateIncompatibleWelcome("sess_b", "room_b"));

            var pumpB = sessionB.PumpOnceAsync(CancellationToken.None);
            while (!pumpB.IsCompleted) yield return null;

            Assert.AreEqual(TableConnectionState.Faulted, sessionB.State);
            StringAssert.Contains("PROTOCOL_MAJOR_UNSUPPORTED", sessionB.LastError);

            coordB.Publish(2, new SessionViewState(
                2,
                sessionB.State,
                sessionB.Binding,
                null,
                null,
                null,
                Array.Empty<PendingCommandSnapshot>(),
                sessionB.LastError));

            for (int i = 0; i < 50 && !incompRoot.activeSelf; i++)
            {
                dispatcherB.Update();
                yield return null;
            }

            Assert.IsTrue(incompRoot.activeSelf, "Incompatible banner must be active when server protocol is incompatible.");
            StringAssert.Contains("服务端协议版本不兼容", incompText.text);

            var shutdownTaskB = coordB.ShutdownAsync("protocol_fault");
            while (!shutdownTaskB.IsCompleted) yield return null;
            Assert.IsNotNull(shutdownTaskB.Result);
            Assert.IsTrue(shutdownTaskB.Result.MenuAllowed);
            channelB.Dispose();
        }

        [UnityTest]
        public IEnumerator Presenter_EnableDisableDestroy_Matrix()
        {
            var channel = new PlayModeScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var queue = new TableSessionOperationQueue(session);
            var dispatcher = new MainThreadDispatcher();
            var coord = new SessionCoordinator(session, queue, channel, dispatcher, new AppConfig(), generation: 1);

            var presenterGo = Go("MatrixSpectatorPresenter");
            var presenter = presenterGo.AddComponent<SpectatorPresenter>();
            var wallText = Text();
            presenter.Configure(wallCountText: wallText);
            presenter.Rebind(coord);

            _ = queue.OpenAsync(new TableSessionOpenRequest("tm", "cm"), CancellationToken.None);
            while (!channel.IsOpen) yield return null;

            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_m", "room_m", Role.Spectator, -1, 1));
            var pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session.State);

            var syncId = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_m", "room_m", 1, 68, syncId));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session.State);

            for (int i = 0; i < 50 && string.IsNullOrEmpty(wallText.text); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("68", wallText.text);

            // 1. Disable presenter -> OnDisable clears display and unhooks Changed
            presenter.enabled = false;
            Assert.IsEmpty(wallText.text, "Disabling presenter must clear display");

            // Push while disabled
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush("sess_m", "room_m", 1, 52, projectionSeq: 2, stateVersion: 2, eventSeq: 102));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            dispatcher.Update();
            yield return null;

            // Still empty because presenter was disabled and ignored session events
            Assert.IsEmpty(wallText.text, "Presenter must not receive or render pushes while disabled");

            // 2. Re-enable presenter -> OnEnable re-hooks and syncs current state
            presenter.enabled = true;
            dispatcher.Update();
            yield return null;
            StringAssert.Contains("52", wallText.text, "Re-enabling presenter restores state from current session view");

            // New push updates presenter
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush("sess_m", "room_m", 1, 48, projectionSeq: 3, stateVersion: 3, eventSeq: 103));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            for (int i = 0; i < 50 && !wallText.text.Contains("48"); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("48", wallText.text);

            // 3. Destroy presenter -> session events must not throw
            UnityEngine.Object.DestroyImmediate(presenterGo);
            _owned.Remove(presenterGo);

            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush("sess_m", "room_m", 1, 40, projectionSeq: 4, stateVersion: 4, eventSeq: 104));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            Assert.DoesNotThrow(() => dispatcher.Update());

            var shutdownM = coord.ShutdownAsync("matrix_done");
            while (!shutdownM.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownM.Result.Outcome);
            Assert.IsFalse(shutdownM.Result.Quarantined);
            channel.Dispose();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_SerializedScene_EndToEndStream()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var loadTask = root.Router.LoadAsync(SceneId.Spectator);
            while (!loadTask.IsCompleted) yield return null;
            Assert.IsTrue(loadTask.Result);

            var scenePresenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(scenePresenter, "Serialized SpectatorPresenter must exist in Spectator scene.");
            Assert.IsNotNull(scenePresenter!.WallCountText, "WallCountText must be wired in serialized scene.");
            Assert.IsNotNull(scenePresenter.ConnectionBanner, "ConnectionBanner must be wired in serialized scene.");
            Assert.IsNotNull(scenePresenter.BackButton, "BackButton must be wired in serialized scene.");

            var channel = new PlayModeScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var queue = new TableSessionOperationQueue(session);
            var dispatcher = new MainThreadDispatcher();
            var coord = new SessionCoordinator(session, queue, channel, dispatcher, new AppConfig(), generation: 1);

            scenePresenter.Rebind(coord);

            _ = queue.OpenAsync(new TableSessionOpenRequest("tScene", "cScene"), CancellationToken.None);
            while (!channel.IsOpen) yield return null;

            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_sc", "room_sc", Role.Spectator, -1, 1));
            var pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            var syncId = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_sc", "room_sc", 1, 68, syncId));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            for (int i = 0; i < 50 && string.IsNullOrEmpty(scenePresenter.WallCountText.text); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("68", scenePresenter.WallCountText.text, "Serialized scene WallCountText must receive 68 from real channel");
            var bannerText = scenePresenter.ConnectionBanner!.GetComponentInChildren<TextMeshProUGUI>(true);
            Assert.AreEqual("已连接", bannerText.text, "Serialized scene ConnectionBanner must show '已连接'");

            // Push 54
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush("sess_sc", "room_sc", 1, 54, projectionSeq: 2, stateVersion: 2, eventSeq: 102));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            for (int i = 0; i < 50 && !scenePresenter.WallCountText.text.Contains("54"); i++)
            {
                dispatcher.Update();
                yield return null;
            }

            StringAssert.Contains("54", scenePresenter.WallCountText.text, "Serialized scene WallCountText must update to 54");

            // Click scene back button
            scenePresenter.BackButton!.onClick.Invoke();
            yield return null;

            float timeout = 3f;
            while (root.Router.CurrentScene != SceneId.Menu && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            Assert.AreEqual(SceneId.Menu, root.Router.CurrentScene, "Clicking BackButton in serialized scene must navigate to Menu");

            var closeTask = coord.ShutdownAsync("scene_test_ended");
            while (!closeTask.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, closeTask.Result.Outcome);
            Assert.IsFalse(closeTask.Result.Quarantined);
            channel.Dispose();
        }

        [UnityTest]
        public IEnumerator RealClientTableSession_Reconnect_NewBaseline_And_StaleBindingEpochRejection()
        {
            var channel = new PlayModeScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var queue = new TableSessionOperationQueue(session);
            var dispatcher = new MainThreadDispatcher();
            var coord = new SessionCoordinator(session, queue, channel, dispatcher, new AppConfig(), generation: 1);

            var presenterGo = Go("SpectatorReconPresenter");
            var presenter = presenterGo.AddComponent<SpectatorPresenter>();
            var wallText = Text();
            presenter.Configure(wallCountText: wallText);
            presenter.Rebind(coord);

            // Phase 1: Initial connection with bindingEpoch 1
            _ = queue.OpenAsync(new TableSessionOpenRequest("t1", "c1"), CancellationToken.None);
            while (session.State != TableConnectionState.Handshaking) yield return null;

            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_r", "room_r", Role.Spectator, -1, bindingEpoch: 1));
            var pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Synchronizing, session.State);

            var syncId1 = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_r", "room_r", bindingEpoch: 1, wallRemaining: 68, correlationId: syncId1));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session.State);
            Assert.AreEqual(1ul, session.Binding!.BindingEpoch.Value);

            for (int i = 0; i < 50 && string.IsNullOrEmpty(wallText.text); i++)
            {
                dispatcher.Update();
                yield return null;
            }
            StringAssert.Contains("68", wallText.text, "Initial connection must display 68 remaining wall tiles");

            // Phase 2: Disconnect and Reconnect establishing new atomic baseline with bindingEpoch 2
            channel.EnqueueException(new TableChannelClosedException(ProtocolErrorCode.HeartbeatTimeout, false));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Reconnecting, session.State, "Transient transport failure must transition session to Reconnecting");

            var openTask2 = queue.OpenAsync(new TableSessionOpenRequest("t2", "c2"), CancellationToken.None);
            while (session.State != TableConnectionState.Handshaking) yield return null;

            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreateWelcome("sess_r", "room_r", Role.Spectator, -1, bindingEpoch: 2));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(2ul, session.Binding!.BindingEpoch.Value, "Reconnected session must adopt new bindingEpoch 2");

            var syncId2 = channel.SentEnvelopes[channel.SentEnvelopes.Count - 1].CorrelationId;
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicSync("sess_r", "room_r", bindingEpoch: 2, wallRemaining: 44, correlationId: syncId2));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;
            Assert.AreEqual(TableConnectionState.Ready, session.State);

            for (int i = 0; i < 50 && !wallText.text.Contains("44"); i++)
            {
                dispatcher.Update();
                yield return null;
            }
            StringAssert.Contains("44", wallText.text, "New baseline must update wall count to 44");

            // Phase 3: Stale frame with old bindingEpoch 1 arrives on the real channel
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePublicPush("sess_r", "room_r", bindingEpoch: 1, wallRemaining: 99));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            dispatcher.Update();
            yield return null;
            Assert.IsFalse(wallText.text.Contains("99"), "Stale bindingEpoch 1 frame must be rejected by TableSession");
            StringAssert.Contains("44", wallText.text, "Display must remain at 44 from current atomic baseline");

            // Phase 4: Stale player push with cheat points arrives with old bindingEpoch 1
            channel.EnqueueEnvelope(PlayModeScriptedTableChannel.CreatePlayerPush("sess_r", "room_r", seatId: 0, bindingEpoch: 1, cheatPoints: 9999));
            pump = session.PumpOnceAsync(CancellationToken.None);
            while (!pump.IsCompleted) yield return null;

            dispatcher.Update();
            yield return null;
            Assert.AreEqual(2ul, session.Binding!.BindingEpoch.Value);

            // Phase 5: Clean shutdown barrier
            var shutdownTask = coord.ShutdownAsync("reconnect_test_completed");
            while (!shutdownTask.IsCompleted) yield return null;
            Assert.AreEqual(ShutdownOutcome.Completed, shutdownTask.Result.Outcome);
            Assert.IsFalse(shutdownTask.Result.Quarantined);
            Assert.IsTrue(shutdownTask.Result.MenuAllowed);
            channel.Dispose();
        }

        [UnityTest]
        public IEnumerator SpectatorPresenter_Subscription_EnableDisableDestroy_Matrix()
        {
            var obsView = new ObservableSessionView();
            var presenterGo = Go("ObsSpectatorPresenter");
            var presenter = presenterGo.AddComponent<SpectatorPresenter>();
            var wallText = Text();
            presenter.Configure(wallCountText: wallText);

            Assert.AreEqual(0, obsView.ListenerCount, "Before rebind, listener count must be 0");
            presenter.Rebind(obsView);
            Assert.AreEqual(1, obsView.ListenerCount, "After rebind, listener count must be 1");
            var oldDelegate1 = obsView.CapturedDelegate;
            Assert.IsNotNull(oldDelegate1, "Captured delegate from view1 must be non-null");

            // 1. Disable presenter -> OnDisable unhooks Changed and clears display
            presenter.enabled = false;
            Assert.AreEqual(0, obsView.ListenerCount, "When disabled, presenter must unhook from session Changed");
            Assert.IsEmpty(wallText.text, "Disabling presenter must clear display");

            // Invoking captured old delegate while disabled must have no effect
            var publicSnap52 = PlayModeScriptedTableChannel.CreatePublicPush("s1", "r1", 1, 52).PublicUpdate.Snapshot;
            var bindingSpectator = PlayModeScriptedTableChannel.CreateBinding("s1", "r1");
            var state52 = new SessionViewState(1, TableConnectionState.Ready, bindingSpectator, null, publicSnap52, null, null, null);
            oldDelegate1!.Invoke(obsView, state52);
            Assert.IsEmpty(wallText.text, "Presenter must not receive or process pushes while disabled");

            // 2. Re-enable presenter -> OnEnable re-hooks and syncs current state
            obsView.Current = state52;
            presenter.enabled = true;
            Assert.AreEqual(1, obsView.ListenerCount, "Re-enabling presenter must re-hook to session Changed");
            StringAssert.Contains("52", wallText.text, "Re-enabling presenter restores state from current session view");

            // 3. Rebind to new view -> Old delegate from view1 must be isolated and ignored
            var obsView2 = new ObservableSessionView();
            var publicSnap36 = PlayModeScriptedTableChannel.CreatePublicPush("s2", "r2", 1, 36).PublicUpdate.Snapshot;
            var binding2 = PlayModeScriptedTableChannel.CreateBinding("s2", "r2");
            obsView2.Current = new SessionViewState(2, TableConnectionState.Ready, binding2, null, publicSnap36, null, null, null);
            presenter.Rebind(obsView2);
            Assert.AreEqual(1, obsView2.ListenerCount, "New view must have 1 listener");
            StringAssert.Contains("36", wallText.text);

            // Calling captured old delegate from view1 with fake high-generation state
            var fakeState = new SessionViewState(99, TableConnectionState.Ready, bindingSpectator, null, PlayModeScriptedTableChannel.CreatePublicPush("s1", "r1", 1, 99).PublicUpdate.Snapshot, null, null, null);
            oldDelegate1.Invoke(obsView, fakeState);
            Assert.IsFalse(wallText.text.Contains("99"), "Old delegate from view1 must NOT affect presenter rebound to view2");
            StringAssert.Contains("36", wallText.text);

            // 4. Destroy presenter -> OnDestroy unhooks
            UnityEngine.Object.DestroyImmediate(presenterGo);
            _owned.Remove(presenterGo);
            Assert.AreEqual(0, obsView2.ListenerCount, "Destroying presenter must unhook from session Changed");

            // Invoking old delegate after destroy does not throw
            Assert.DoesNotThrow(() => oldDelegate1.Invoke(obsView, fakeState));
            Assert.DoesNotThrow(() => obsView2.Trigger(fakeState));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SettlementPresenter_Subscription_EnableDisableDestroy_Matrix()
        {
            var obsView = new ObservableSessionView();
            var presenterGo = Go("ObsSettlementPresenter");
            var presenter = presenterGo.AddComponent<SettlementPresenter>();
            var winText = Text();

            var ledgerGo = Go("ObsLedgerRoot");
            var ledgerPresenter = ledgerGo.AddComponent<PrivateLedgerPresenter>();
            var balanceText = Text();
            var entriesText = Text();
            var authText = Text();
            var localTag = Text();
            ledgerPresenter.Configure(ledgerGo, balanceText, entriesText, authText, localTag);

            presenter.Configure(winSummaryText: winText, privateLedger: ledgerPresenter);

            Assert.AreEqual(0, obsView.ListenerCount, "Before rebind, listener count must be 0");
            presenter.Rebind(obsView);
            Assert.AreEqual(1, obsView.ListenerCount, "After rebind, listener count must be 1");
            var oldDelegate1 = obsView.CapturedDelegate;
            Assert.IsNotNull(oldDelegate1, "Captured delegate must be non-null");

            // Step A: Feed authorized Player state with 500 cheat points and 3900 win
            var snapWithWin = new PublicSnapshot
            {
                SessionId = "s1",
                RoundId = "round_1",
                StateVersion = 2,
                EventSeq = 102,
                Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 2 },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView { WallRemaining = 40, Phase = GamePhase.RoundEnd },
                    SettlementRoundId = "round_1"
                }
            };
            snapWithWin.View.SettlementEvents.Add(new PublicEventEnvelope
            {
                EventType = Mahjong.Contracts.Wire.EventType.RonWin,
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                Visibility = EventVisibility.SettlementPublic,
                RonWin = new RonWin { Winner = 0, Loser = 1, Score = 3900, Han = 2, Fu = 30 }
            });

            var playerBinding = PlayModeScriptedTableChannel.CreateBinding("s1", "r1", Mahjong.Contracts.ProtocolRole.Player, 0);
            var playerSnap = PlayModeScriptedTableChannel.CreatePlayerSync("s1", "r1", 0, 1, 500).SyncResponse.Player;
            var stateAuthorized = new SessionViewState(1, TableConnectionState.Ready, playerBinding, playerSnap, snapWithWin, null, null, null);

            obsView.Trigger(stateAuthorized);
            Assert.IsTrue(ledgerPresenter.IsVisible, "Authorized Player must see private ledger");
            StringAssert.Contains("500", balanceText.text);
            StringAssert.Contains("3900", winText.text);

            // Step B: De-authorize / revoke by switching to Spectator role -> assert ALL ledger texts cleared
            var spectatorBinding = PlayModeScriptedTableChannel.CreateBinding("s1", "r1", Mahjong.Contracts.ProtocolRole.Spectator, -1);
            var stateRevoked = new SessionViewState(2, TableConnectionState.Ready, spectatorBinding, null, snapWithWin, null, null, null);
            obsView.Trigger(stateRevoked);

            Assert.IsFalse(ledgerPresenter.IsVisible, "Revoking authorization must hide private ledger");
            Assert.IsEmpty(balanceText.text, "BalanceText must be cleared when authorization is revoked");
            Assert.IsEmpty(entriesText.text, "EntriesText must be cleared when authorization is revoked");
            Assert.IsEmpty(authText.text, "AuthStatusText must be cleared when authorization is revoked");
            Assert.IsEmpty(localTag.text, "LocalGameTag must be cleared when authorization is revoked");

            // Step C: Disable presenter -> display cleared; invoking old delegate while disabled must NOT restore
            presenter.enabled = false;
            Assert.AreEqual(0, obsView.ListenerCount, "When disabled, settlement presenter must unhook");
            Assert.IsEmpty(winText.text, "Disabling settlement presenter must clear display");

            oldDelegate1!.Invoke(obsView, stateAuthorized);
            Assert.IsFalse(ledgerPresenter.IsVisible, "Old delegate must NOT re-activate private ledger when presenter is disabled");
            Assert.IsEmpty(winText.text, "Old delegate must NOT populate win text when presenter is disabled");

            // Step D: Re-enable presenter -> re-hooks and syncs current state (revoked)
            presenter.enabled = true;
            Assert.AreEqual(1, obsView.ListenerCount, "Re-enabling settlement presenter must re-hook to session Changed");
            Assert.IsFalse(ledgerPresenter.IsVisible, "Re-enabled presenter with revoked current state must remain hidden");
            StringAssert.Contains("3900", winText.text);

            // Step E: Rebind to view2 -> Old delegate from view1 must be completely isolated
            var obsView2 = new ObservableSessionView();
            presenter.Rebind(obsView2);
            Assert.AreEqual(1, obsView2.ListenerCount);

            oldDelegate1.Invoke(obsView, stateAuthorized);
            Assert.IsFalse(ledgerPresenter.IsVisible, "Old delegate from view1 must NOT re-activate ledger after rebind to view2");

            // Step F: Destroy presenter -> unhooked, no exceptions
            UnityEngine.Object.DestroyImmediate(presenterGo);
            _owned.Remove(presenterGo);
            Assert.AreEqual(0, obsView2.ListenerCount, "Destroying settlement presenter must unhook from session Changed");

            Assert.DoesNotThrow(() => oldDelegate1.Invoke(obsView, stateAuthorized));
            Assert.DoesNotThrow(() => obsView2.Trigger(stateAuthorized));
            yield return null;
        }

        private sealed class ObservableSessionView : ISessionView
        {
            public SessionViewState Current { get; set; } = new SessionViewState(0, TableConnectionState.Ready, null, null, null, null, null, null);
            public event EventHandler<SessionViewState>? Changed;

            public int ListenerCount => Changed?.GetInvocationList().Length ?? 0;
            public EventHandler<SessionViewState>? CapturedDelegate => Changed;

            public void Trigger(SessionViewState state)
            {
                Current = state;
                Changed?.Invoke(this, state);
            }
        }
    }
}
