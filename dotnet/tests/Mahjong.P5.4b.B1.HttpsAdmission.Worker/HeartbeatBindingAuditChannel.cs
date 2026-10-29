using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts.Wire;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// TEST-ONLY observation point inside the production heartbeat decorator.
    ///
    /// It wraps the real transport (ReceiptLossChannel -> RealWssTableChannel) and
    /// never creates, answers, drops, reorders or rewrites a frame. It only decodes
    /// a copy of each passing frame so the B1 suite can prove, over multiple real
    /// server heartbeat periods, that:
    ///
    /// - every server PING carries the WELCOME-admitted session/room/seat/epoch;
    /// - every client PONG echoes a nonce the server actually sent; and
    /// - neither side closed the connection unexpectedly.
    ///
    /// The production Client still performs the strict accept/reject decision; this
    /// tap exists to make the correlation observable, not to relax it.
    /// </summary>
    public sealed class HeartbeatBindingAuditChannel : ITableChannel
    {
        private readonly ITableChannel _inner;
        private readonly object _gate = new object();
        private readonly HashSet<string> _pendingPings = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _serverPingNonces = new List<string>();
        private readonly List<string> _matchedPongNonces = new List<string>();

        private bool _bound;
        private string? _boundSessionId;
        private string? _boundRoomId;
        private int _boundSeatId;
        private ulong _boundBindingEpoch;

        private int _serverPingCount;
        private int _matchedPongCount;
        private int _unmatchedPongCount;
        private int _pingBindingMismatchCount;
        private int _pongBindingMismatchCount;
        private int _emptyIdentityPingCount;
        private int _inboundCloseEnvelopeCount;
        private int _closeCallCount;
        private int _transportCloseCount;

        private string? _lastPingNonce;
        private string? _lastPongNonce;
        private string? _lastMismatch;
        private string? _lastCloseCode;

        public HeartbeatBindingAuditChannel(ITableChannel inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool IsOpen => _inner.IsOpen;

        public int ServerPingCount
        {
            get { lock (_gate) { return _serverPingCount; } }
        }

        public int MatchedPongCount
        {
            get { lock (_gate) { return _matchedPongCount; } }
        }

        public int UnmatchedPongCount
        {
            get { lock (_gate) { return _unmatchedPongCount; } }
        }

        public int PingBindingMismatchCount
        {
            get { lock (_gate) { return _pingBindingMismatchCount; } }
        }

        public int PongBindingMismatchCount
        {
            get { lock (_gate) { return _pongBindingMismatchCount; } }
        }

        public int EmptyIdentityPingCount
        {
            get { lock (_gate) { return _emptyIdentityPingCount; } }
        }

        public int InboundCloseEnvelopeCount
        {
            get { lock (_gate) { return _inboundCloseEnvelopeCount; } }
        }

        public int CloseCallCount
        {
            get { lock (_gate) { return _closeCallCount; } }
        }

        public int TransportCloseCount
        {
            get { lock (_gate) { return _transportCloseCount; } }
        }

        /// <summary>
        /// Atomic snapshot: counts and their ordered nonce ledgers are copied under one
        /// lock, so a phase baseline can never pair a count from one instant with a nonce
        /// list from another while a receive/send is being observed.
        /// </summary>
        public HeartbeatAuditCounters CaptureCounters()
        {
            lock (_gate)
            {
                return new HeartbeatAuditCounters(
                    _serverPingCount,
                    _matchedPongCount,
                    _pingBindingMismatchCount,
                    _pongBindingMismatchCount,
                    _emptyIdentityPingCount,
                    _unmatchedPongCount,
                    _closeCallCount,
                    _inboundCloseEnvelopeCount,
                    _transportCloseCount,
                    _serverPingNonces.ToArray(),
                    _matchedPongNonces.ToArray());
            }
        }

        public string? LastPingNonce
        {
            get { lock (_gate) { return _lastPingNonce; } }
        }

        public string? LastPongNonce
        {
            get { lock (_gate) { return _lastPongNonce; } }
        }

        public string? LastMismatch
        {
            get { lock (_gate) { return _lastMismatch; } }
        }

        public string? LastCloseCode
        {
            get { lock (_gate) { return _lastCloseCode; } }
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _bound = false;
                _boundSessionId = null;
                _boundRoomId = null;
                _boundSeatId = 0;
                _boundBindingEpoch = 0;
                _pendingPings.Clear();
                _serverPingNonces.Clear();
                _matchedPongNonces.Clear();
                _serverPingCount = 0;
                _matchedPongCount = 0;
                _unmatchedPongCount = 0;
                _pingBindingMismatchCount = 0;
                _pongBindingMismatchCount = 0;
                _emptyIdentityPingCount = 0;
                _inboundCloseEnvelopeCount = 0;
                _closeCallCount = 0;
                _transportCloseCount = 0;
                _lastPingNonce = null;
                _lastPongNonce = null;
                _lastMismatch = null;
                _lastCloseCode = null;
            }

            return _inner.OpenAsync(request, cancellationToken);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            await _inner.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
            ObserveOutbound(wireEnvelope.Span);
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte> wireEnvelope;
            try
            {
                wireEnvelope = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException ex)
            {
                lock (_gate)
                {
                    _transportCloseCount++;
                    _lastCloseCode = ex.Code;
                }

                throw;
            }

            ObserveInbound(wireEnvelope.Span);
            return wireEnvelope;
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _closeCallCount++;
                _lastCloseCode = reason.Code;
            }

            return _inner.CloseAsync(reason, cancellationToken);
        }

        private void ObserveInbound(ReadOnlySpan<byte> wireEnvelope)
        {
            WireDecodeResult decoded = WireCodec.Decode(wireEnvelope);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                return;
            }

            Envelope envelope = decoded.Envelope;
            if (envelope.MessageType == MessageType.Welcome && envelope.Welcome != null)
            {
                lock (_gate)
                {
                    _bound = true;
                    _boundSessionId = envelope.Welcome.SessionId;
                    _boundRoomId = envelope.Welcome.RoomId;
                    _boundSeatId = envelope.Welcome.SeatId;
                    _boundBindingEpoch = envelope.Welcome.BindingEpoch;
                }

                return;
            }

            if (envelope.MessageType == MessageType.Close)
            {
                lock (_gate)
                {
                    _inboundCloseEnvelopeCount++;
                    _lastCloseCode = envelope.Close?.Code;
                }

                return;
            }

            if (envelope.MessageType != MessageType.Heartbeat ||
                envelope.Heartbeat == null ||
                envelope.Heartbeat.BeatCase != Heartbeat.BeatOneofCase.Ping)
            {
                return;
            }

            string nonce = envelope.Heartbeat.Ping.Nonce;
            lock (_gate)
            {
                _serverPingCount++;
                _serverPingNonces.Add(nonce);
                _lastPingNonce = nonce;
                _pendingPings.Add(nonce);
                if (!_bound ||
                    string.IsNullOrEmpty(_boundSessionId) ||
                    string.IsNullOrEmpty(_boundRoomId) ||
                    !string.Equals(envelope.SessionId, _boundSessionId, StringComparison.Ordinal) ||
                    !string.Equals(envelope.RoomId, _boundRoomId, StringComparison.Ordinal) ||
                    envelope.SeatId != _boundSeatId ||
                    envelope.BindingEpoch != _boundBindingEpoch)
                {
                    _pingBindingMismatchCount++;
                    _lastMismatch = "ping-binding";
                }

                if (string.IsNullOrEmpty(envelope.SessionId) || string.IsNullOrEmpty(envelope.RoomId))
                {
                    _emptyIdentityPingCount++;
                }
            }
        }

        private void ObserveOutbound(ReadOnlySpan<byte> wireEnvelope)
        {
            WireDecodeResult decoded = WireCodec.Decode(wireEnvelope);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                return;
            }

            Envelope envelope = decoded.Envelope;
            if (envelope.MessageType != MessageType.Heartbeat ||
                envelope.Heartbeat == null ||
                envelope.Heartbeat.BeatCase != Heartbeat.BeatOneofCase.Pong)
            {
                return;
            }

            string nonce = envelope.Heartbeat.Pong.Nonce;
            lock (_gate)
            {
                _lastPongNonce = nonce;
                if (_pendingPings.Remove(nonce))
                {
                    _matchedPongCount++;
                    _matchedPongNonces.Add(nonce);
                }
                else
                {
                    _unmatchedPongCount++;
                }

                if (!_bound ||
                    string.IsNullOrEmpty(_boundSessionId) ||
                    string.IsNullOrEmpty(_boundRoomId) ||
                    !string.Equals(envelope.SessionId, _boundSessionId, StringComparison.Ordinal) ||
                    !string.Equals(envelope.RoomId, _boundRoomId, StringComparison.Ordinal) ||
                    envelope.SeatId != _boundSeatId ||
                    envelope.BindingEpoch != _boundBindingEpoch)
                {
                    _pongBindingMismatchCount++;
                    _lastMismatch = "pong-binding";
                }
            }
        }
    }
}
