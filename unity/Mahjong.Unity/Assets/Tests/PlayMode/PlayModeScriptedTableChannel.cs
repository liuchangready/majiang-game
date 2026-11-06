#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;
using Mahjong.Contracts.Wire;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class PlayModeScriptedTableChannel : ITableChannel, IDisposable
    {
        private readonly Queue<ReadOnlyMemory<byte>> _inbound = new Queue<ReadOnlyMemory<byte>>();
        private readonly List<byte[]> _outbound = new List<byte[]>();
        private Exception? _pendingException;
        private bool _isOpen;
        private bool _isDisposed;

        public bool IsOpen => _isOpen;
        public bool IsDisposed => _isDisposed;
        public IReadOnlyList<byte[]> OutboundMessages => _outbound;

        public void EnqueueException(Exception ex)
        {
            _pendingException = ex;
        }

        public IReadOnlyList<Envelope> SentEnvelopes
        {
            get
            {
                var list = new List<Envelope>(_outbound.Count);
                for (int i = 0; i < _outbound.Count; i++)
                {
                    var res = WireCodec.Decode(_outbound[i]);
                    if (res.IsOk && res.Envelope != null)
                    {
                        list.Add(res.Envelope);
                    }
                }
                return list;
            }
        }

        public void EnqueueInbound(byte[] bytes)
        {
            _inbound.Enqueue(bytes);
        }

        public void EnqueueEnvelope(Envelope envelope)
        {
            EnqueueInbound(WireCodec.Encode(envelope));
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            _isOpen = true;
            _pendingException = null;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            _outbound.Add(wireEnvelope.ToArray());
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<ReadOnlyMemory<byte>>(cancellationToken);
            }

            if (_pendingException != null)
            {
                var ex = _pendingException;
                _pendingException = null;
                return Task.FromException<ReadOnlyMemory<byte>>(ex);
            }

            if (_inbound.Count > 0)
            {
                return Task.FromResult(_inbound.Dequeue());
            }

            var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            _isOpen = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _isDisposed = true;
            _isOpen = false;
        }

        public static TableSessionBinding CreateBinding(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            Mahjong.Contracts.ProtocolRole role = Mahjong.Contracts.ProtocolRole.Spectator,
            int seat = -1)
        {
            return new TableSessionBinding(
                new Mahjong.Contracts.SessionId(sessionId),
                new Mahjong.Contracts.RoomId(roomId),
                "table_1",
                role,
                seat,
                new Mahjong.Contracts.ConnectionEpoch(1),
                new Mahjong.Contracts.OwnerEpoch(1),
                new Mahjong.Contracts.BindingEpoch(1),
                new Mahjong.Contracts.RequestScope(new Mahjong.Contracts.RequestScopeId("scope_1"), "account_1", role, new Mahjong.Contracts.RoomId(roomId), 1000, 2000),
                new Mahjong.Contracts.StreamBaseline(new Mahjong.Contracts.StateVersion(1), new Mahjong.Contracts.EventSeq(1), new Mahjong.Contracts.StreamCursor(new Mahjong.Contracts.StreamVersion(1), new Mahjong.Contracts.ProjectionSeq(1))));
        }

        public static Envelope CreateWelcome(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            Role role = Role.Spectator,
            int seatId = -1,
            int bindingEpoch = 1)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Welcome,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = seatId,
                BindingEpoch = (ulong)bindingEpoch,
                Welcome = new Welcome
                {
                    SessionId = sessionId,
                    RoomId = roomId,
                    TableId = "table_1",
                    Role = role,
                    SeatId = seatId,
                    ConnectionEpoch = 1,
                    OwnerEpoch = 1,
                    BindingEpoch = (ulong)bindingEpoch,
                    Baseline = new StreamBaseline
                    {
                        StateVersion = 1,
                        EventSeq = 101,
                        Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 }
                    },
                    Scope = new RequestScope
                    {
                        ScopeId = "scope_1",
                        AccountId = "account_1",
                        Role = role,
                        RoomId = roomId,
                        IssuedAtMs = 1000,
                        ExpiresAtMs = 2000
                    },
                    Limits = new ServerLimits
                    {
                        MaxInboundMessageBytes = 1024 * 1024,
                        MaxCommandPayloadBytes = 64 * 1024,
                        MaxSnapshotBytes = 1024 * 1024,
                        MaxInFlightRequests = 64,
                        HeartbeatIntervalMs = 10000,
                        IdleDeadMs = 30000,
                        MaxEarlyBufferItems = 64,
                        MaxEarlyBufferBytes = 4 * 1024 * 1024
                    },
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion
                }
            };
        }

        public static Envelope CreatePublicSync(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            int bindingEpoch = 1,
            uint wallRemaining = 68,
            string? correlationId = null)
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = sessionId,
                RoundId = "round_1",
                StateVersion = 1,
                EventSeq = 101,
                Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = wallRemaining,
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0
                    },
                    Session = new PublicSessionSummary { Finished = false },
                    SettlementRoundId = "round_1"
                }
            };
            for (int i = 0; i < 4; i++)
            {
                snapshot.View.Board.Players.Add(new PlayerPublicView
                {
                    Seat = i,
                    HandCount = 13,
                    Riichi = false
                });
                snapshot.View.Board.Scores.Add(new ScoreView
                {
                    Seat = i,
                    Score = 25000
                });
            }

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = -1,
                BindingEpoch = (ulong)bindingEpoch,
                CorrelationId = correlationId ?? string.Empty,
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Public = snapshot,
                    StateVersion = 1,
                    EventSeq = 101
                }
            };
        }

        public static Envelope CreatePublicPush(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            int bindingEpoch = 1,
            uint wallRemaining = 54,
            ulong projectionSeq = 2,
            ulong stateVersion = 2,
            ulong eventSeq = 102)
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = sessionId,
                RoundId = "round_1",
                StateVersion = stateVersion,
                EventSeq = eventSeq,
                Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = wallRemaining,
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 1,
                        DealerSeat = 0,
                        CurrentSeat = 1
                    },
                    Session = new PublicSessionSummary { Finished = false },
                    SettlementRoundId = "round_1"
                }
            };
            for (int i = 0; i < 4; i++)
            {
                snapshot.View.Board.Players.Add(new PlayerPublicView
                {
                    Seat = i,
                    HandCount = 13,
                    Riichi = i == 0
                });
                snapshot.View.Board.Scores.Add(new ScoreView
                {
                    Seat = i,
                    Score = 25000
                });
            }

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PublicUpdate,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = -1,
                BindingEpoch = (ulong)bindingEpoch,
                PublicUpdate = new PublicUpdate
                {
                    Snapshot = snapshot,
                    FromCursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq > 0 ? projectionSeq - 1 : 0 },
                    Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq },
                    IsFullSnapshot = false
                }
            };
        }

        public static Envelope CreateIncompatibleWelcome(
            string sessionId = "session_playmode",
            string roomId = "room_playmode")
        {
            var env = CreateWelcome(sessionId, roomId, Role.Spectator, -1, 1);
            env.Welcome.ProtocolMajor = ProtocolConstants.Major + 99;
            return env;
        }

        public static Envelope CreatePlayerSync(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            int seatId = 0,
            int bindingEpoch = 1,
            long cheatPoints = 500,
            string? correlationId = null)
        {
            var snap = new PlayerSnapshot
            {
                SessionId = sessionId,
                RoundId = "round_1",
                StateVersion = 1,
                EventSeq = 101,
                Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                PublicView = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = 60,
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        DealerSeat = 0,
                        CurrentSeat = 0
                    },
                    SettlementRoundId = "round_1"
                },
                PrivateView = new PlayerPrivateView
                {
                    Seat = seatId,
                    Cheat = new PrivateCheatView
                    {
                        CheatPoints = cheatPoints
                    }
                }
            };
            for (int i = 0; i < 4; i++)
            {
                snap.PublicView.Board.Players.Add(new PlayerPublicView
                {
                    Seat = i,
                    HandCount = 13,
                    Riichi = false
                });
                snap.PublicView.Board.Scores.Add(new ScoreView
                {
                    Seat = i,
                    Score = 25000
                });
            }
            snap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "entry_1",
                Amount = -100,
                BalanceAfter = cheatPoints - 100,
                ReasonCode = "TILE_MARK"
            });

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = seatId,
                BindingEpoch = (ulong)bindingEpoch,
                CorrelationId = correlationId ?? string.Empty,
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = snap,
                    StateVersion = 1,
                    EventSeq = 101
                }
            };
        }

        public static Envelope CreatePlayerPush(
            string sessionId = "session_playmode",
            string roomId = "room_playmode",
            int seatId = 0,
            int bindingEpoch = 1,
            long cheatPoints = 500,
            ulong projectionSeq = 2,
            ulong stateVersion = 2,
            ulong eventSeq = 102)
        {
            var env = CreatePlayerSync(sessionId, roomId, seatId, bindingEpoch, cheatPoints);
            var playerSnap = env.SyncResponse.Player;
            playerSnap.StateVersion = stateVersion;
            playerSnap.EventSeq = eventSeq;
            playerSnap.Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq };

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
                BindingEpoch = (ulong)bindingEpoch,
                CorrelationId = string.Empty,
                PlayerUpdate = new PlayerUpdate
                {
                    Snapshot = playerSnap,
                    FromCursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq - 1 },
                    Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = projectionSeq },
                    IsFullSnapshot = false
                }
            };
        }
    }
}
