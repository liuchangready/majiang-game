#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Spectator;
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

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class PrivateLedgerAuthorizationTests
    {
        private TableSessionBinding CreateBinding(
            string sessionId = "sess_player_1",
            string roomId = "room_1",
            ProtocolRole role = ProtocolRole.Player,
            int seat = 0)
        {
            return new TableSessionBinding(
                new SessionId(sessionId),
                new RoomId(roomId),
                "table_1",
                role,
                seat,
                new ConnectionEpoch(1),
                new OwnerEpoch(1),
                new BindingEpoch(1),
                new RequestScope(new RequestScopeId("scope_1"), "account_1", role, new RoomId(roomId), 1000, 2000),
                new StreamBaseline(new StateVersion(1), new EventSeq(1), new StreamCursor(new StreamVersion(1), new ProjectionSeq(1))));
        }

        private PlayerSnapshot CreatePlayerSnapshot(
            string sessionId = "sess_player_1",
            int seat = 0,
            long points = 800)
        {
            var snap = new PlayerSnapshot
            {
                SessionId = sessionId,
                RoundId = "round_E1_0",
                PrivateView = new PlayerPrivateView
                {
                    Seat = seat,
                    Cheat = new PrivateCheatView
                    {
                        CheatPoints = points
                    }
                }
            };

            snap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "e1",
                Amount = -100,
                BalanceAfter = 700,
                ReasonCode = "TILE_MARK"
            });
            snap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "e2",
                Amount = -200,
                BalanceAfter = 500,
                ReasonCode = "BEAM_SWAP"
            });

            return snap;
        }

        [Test]
        public void AuthorizationMatrix_RolePlayer_SeatMatch_SessionMatch_Authorized()
        {
            var binding = CreateBinding("sess_auth_123", "room_1", ProtocolRole.Player, 1);
            var playerSnap = CreatePlayerSnapshot("sess_auth_123", 1, 800);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            Assert.IsTrue(ledger.IsAuthorized, "Must be authorized when Player role, seat and session all match.");
            Assert.AreEqual(1, ledger.AuthorizedSeat);
            Assert.AreEqual("sess_auth_123", ledger.SessionId);
            Assert.AreEqual(800, ledger.CheatPoints);
            Assert.AreEqual(2, ledger.Entries.Count);
            Assert.IsFalse(ledger.IsLocal, "ISessionView supplies no origin; must not invent local.");
            Assert.AreEqual("落汗", ledger.Entries[0].ReasonDisplay);
            Assert.AreEqual("偷梁换柱", ledger.Entries[1].ReasonDisplay);
        }

        [Test]
        public void AuthorizationMatrix_RoleSpectator_Unauthorized_StrictPrivacy()
        {
            var binding = CreateBinding("sess_auth_123", "room_1", ProtocolRole.Spectator, -1);
            var playerSnap = CreatePlayerSnapshot("sess_auth_123", 0, 800);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            Assert.IsFalse(ledger.IsAuthorized, "Spectator role must NEVER access private cheat ledger.");
            Assert.AreEqual(0, ledger.CheatPoints);
            Assert.AreEqual(0, ledger.Entries.Count);
            Assert.AreEqual(-1, ledger.AuthorizedSeat);
        }

        [Test]
        public void AuthorizationMatrix_SeatMismatch_Unauthorized()
        {
            // Binding is seat 1, but player private view claims seat 0
            var binding = CreateBinding("sess_auth_123", "room_1", ProtocolRole.Player, 1);
            var playerSnap = CreatePlayerSnapshot("sess_auth_123", 0, 800);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            Assert.IsFalse(ledger.IsAuthorized, "Seat mismatch must deny access to cheat ledger.");
            Assert.AreEqual(0, ledger.Entries.Count);
        }

        [Test]
        public void AuthorizationMatrix_SessionMismatch_Unauthorized()
        {
            var binding = CreateBinding("sess_new_222", "room_1", ProtocolRole.Player, 0);
            var playerSnap = CreatePlayerSnapshot("sess_old_111", 0, 800);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            Assert.IsFalse(ledger.IsAuthorized, "Session ID mismatch must deny access to cheat ledger.");
            Assert.AreEqual(0, ledger.Entries.Count);
        }

        [Test]
        public void AuthorizationMatrix_NullBindingOrSnapshotOrCheat_Unauthorized()
        {
            var stateNoBinding = new SessionViewState(1, TableConnectionState.Ready, null, null, null, null, null, null);
            var ledger1 = SettlementModelExtractor.ExtractAuthorizedLedger(stateNoBinding);
            Assert.IsFalse(ledger1.IsAuthorized);

            var binding = CreateBinding("sess_1", "room_1", ProtocolRole.Player, 0);
            var snapNoCheat = new PlayerSnapshot
            {
                SessionId = "sess_1",
                PrivateView = new PlayerPrivateView { Seat = 0, Cheat = null }
            };
            var stateNoCheat = new SessionViewState(1, TableConnectionState.Ready, binding, snapNoCheat, null, null, null, null);
            var ledger2 = SettlementModelExtractor.ExtractAuthorizedLedger(stateNoCheat);
            Assert.IsFalse(ledger2.IsAuthorized);
        }

        [Test]
        public void LocalMatch_SafetyFlag_NoExternalRewardOrWalletLeakage()
        {
            var binding = CreateBinding("sess_1", "room_1", ProtocolRole.Player, 0);
            var playerSnap = CreatePlayerSnapshot("sess_1", 0, 1000);

            var state = new SessionViewState(1, TableConnectionState.Ready, binding, playerSnap, null, null, null, null);
            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            Assert.IsTrue(ledger.IsAuthorized);
            Assert.IsFalse(ledger.IsLocal, "ISessionView supplies no origin; unknown origin cannot prove LOCAL.");
        }

        [Test]
        public void DefensiveCopy_SourceMutationDoesNotAffectAuthorizedLedger()
        {
            var binding = CreateBinding("sess_1", "room_1", ProtocolRole.Player, 0);
            var playerSnap = CreatePlayerSnapshot("sess_1", 0, 1000);

            var state = new SessionViewState(1, TableConnectionState.Ready, binding, playerSnap, null, null, null, null);
            var ledger = SettlementModelExtractor.ExtractAuthorizedLedger(state);

            // Mutate source proto list
            playerSnap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "injected",
                Amount = 999,
                BalanceAfter = 1999
            });

            Assert.AreEqual(2, ledger.Entries.Count, "Entries list must be isolated from source mutations.");
        }
    }
}
