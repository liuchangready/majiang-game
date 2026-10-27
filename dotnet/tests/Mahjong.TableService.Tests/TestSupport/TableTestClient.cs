using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Google.Protobuf;

namespace Mahjong.TableService.Tests
{
    /// <summary>One received item: either a decoded envelope or the transport close frame.</summary>
    public readonly struct TableWireMessage
    {
        private TableWireMessage(Envelope? envelope, int? wsCloseCode, string? wsCloseReason)
        {
            Envelope = envelope;
            WsCloseCode = wsCloseCode;
            WsCloseReason = wsCloseReason;
        }

        public static TableWireMessage Of(Envelope envelope) => new(envelope, null, null);

        public static TableWireMessage OfClose(int code, string reason) => new(null, code, reason);

        public Envelope? Envelope { get; }

        public int? WsCloseCode { get; }

        public string? WsCloseReason { get; }

        public bool IsClose => Envelope == null;

        public MessageType Type => Envelope?.MessageType ?? MessageType.Unspecified;
    }

    /// <summary>
    /// Raw ClientWebSocket test client. It speaks exactly the V5 wire contract (one complete
    /// binary message = one Envelope) and can deliberately break it (fragmentation, concat,
    /// oversize, text frames) because the negatives are the point.
    /// </summary>
    public sealed class TableTestClient : IAsyncDisposable
    {
        private const int MaxTestInboundBytes = 4 * 1024 * 1024;

        private readonly WebSocket _ws;

        private long _lastBaselineStateVersion;

        /// <summary>Decision ids this client already handed to a consumer, per seat. The
        /// publish path delivers every open decision twice (transition delta plus the
        /// follow-up full snapshot), so a seat's NEXT-decision wait must be able to
        /// exclude the decision a previous wait already returned — otherwise the twin of
        /// an OLD turn resurfaces as the fresh candidate set under load.</summary>
        private readonly HashSet<string>[] _seenDecisionIdsPerSeat =
            new HashSet<string>[4] { new(), new(), new(), new() };

        private readonly object _seenDecisionGate = new();

        /// <summary>Frames a ReadUntilAsync consumed past without matching. The wire is
        /// consume-only, so without this buffer a frame that failed an earlier predicate
        /// is lost forever and a later wait on the same stream starves under load.</summary>
        private readonly List<TableWireMessage> _replay = new();

        private readonly object _replayGate = new();

        private const int MaxReplayFrames = 512;

        /// <summary>Highest snapshot state version any wait on this client has returned.
        /// A frame OLDER than the watermark is a leftover of an already-consumed past
        /// (river sizes are not monotonic — a claim takes the claimed tile back out) and
        /// must never satisfy a later wait.</summary>
        private long _stateVersionWatermark;

        private TableTestClient(WebSocket ws)
        {
            _ws = ws;
        }

        private static long? SnapshotStateVersion(TableWireMessage message)
        {
            if (message.Envelope?.PlayerUpdate?.Snapshot != null)
            {
                return (long)message.Envelope.PlayerUpdate.Snapshot.StateVersion;
            }

            if (message.Envelope?.PublicUpdate?.Snapshot != null)
            {
                return (long)message.Envelope.PublicUpdate.Snapshot.StateVersion;
            }

            return null;
        }

        private bool IsStale(TableWireMessage message)
        {
            return SnapshotStateVersion(message) is long version &&
                   version < Interlocked.Read(ref _stateVersionWatermark);
        }

        private void AdvanceWatermark(TableWireMessage message)
        {
            if (SnapshotStateVersion(message) is long version)
            {
                long current = Interlocked.Read(ref _stateVersionWatermark);
                while (version > current &&
                       Interlocked.CompareExchange(ref _stateVersionWatermark, version, current) != current)
                {
                    current = Interlocked.Read(ref _stateVersionWatermark);
                }
            }
        }

        /// <summary>Whether the seat's next-decision waits must skip this decision id
        /// (a previous wait on this client already returned it for that seat).</summary>
        public bool IsDecisionSeen(int seat, string decisionId)
        {
            lock (_seenDecisionGate)
            {
                return seat >= 0 && seat <= 3 && _seenDecisionIdsPerSeat[seat].Contains(decisionId);
            }
        }

        /// <summary>Records a decision id as consumed for the seat.</summary>
        public void MarkDecisionSeen(int seat, string decisionId)
        {
            if (seat < 0 || seat > 3)
            {
                return;
            }

            lock (_seenDecisionGate)
            {
                _seenDecisionIdsPerSeat[seat].Add(decisionId);
            }
        }

        public Welcome? Welcome { get; private set; }

        /// <summary>
        /// State version of the most recent full-snapshot stream frame this client has
        /// received (players and spectators both project the same authoritative version).
        /// </summary>
        public ulong ReadLastBaselineStateVersion =>
            (ulong)Interlocked.Read(ref _lastBaselineStateVersion);

        public System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;

        public string? CloseDescription => _ws.CloseStatusDescription;

        public System.Net.WebSockets.WebSocketState State => _ws.State;

        public static async Task<TableTestClient> ConnectAsync(
            string url,
            System.Net.Security.RemoteCertificateValidationCallback? certificateCallback = null)
        {
            var ws = new ClientWebSocket();
            if (certificateCallback != null)
            {
                ws.Options.RemoteCertificateValidationCallback = certificateCallback;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            return new TableTestClient(ws);
        }

        /// <summary>
        /// Connects over a raw socket whose kernel receive buffer is pinned tiny. Loopback
        /// autotuning otherwise absorbs megabytes of undrained server traffic, which makes a
        /// genuine downstream stall unreachable in tests; an explicit SO_RCVBUF (set before
        /// connect, which also disables receive-window autotuning) makes the server's send
        /// path block deterministically once a few hundred frames go undrained. The WebSocket
        /// session itself is identical: manual upgrade handshake, then the same wire protocol
        /// through <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>.
        /// </summary>
        public static async Task<TableTestClient> ConnectOverThrottledSocketAsync(
            string url,
            int receiveBufferSize)
        {
            Uri uri = new Uri(url);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveBufferSize = receiveBufferSize;
            await socket.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);

            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            string request =
                "GET " + uri.PathAndQuery + " HTTP/1.1\r\n" +
                "Host: " + uri.Authority + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes).ConfigureAwait(false);
            string response = await ReadHttpHeadersAsync(stream).ConfigureAwait(false);
            if (!response.StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "WebSocket upgrade over the raw socket failed: " + response.Split('\r')[0]);
            }

            WebSocket ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = Timeout.InfiniteTimeSpan
            });
            return new TableTestClient(ws);
        }

        /// <summary>Reads one HTTP header block (byte-at-a-time; headers are ASCII and small).</summary>
        private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[1];
            var builder = new StringBuilder();
            while (true)
            {
                int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("The server closed during the WebSocket upgrade.");
                }

                builder.Append((char)buffer[0]);
                string text = builder.ToString();
                if (text.Length >= 4 && text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    return text;
                }

                if (builder.Length > 16 * 1024)
                {
                    throw new InvalidOperationException("Upgrade response headers exceeded 16KB.");
                }
            }
        }

        /// <summary>Sends HELLO and consumes the WELCOME (identity captured for later echoes).</summary>
        public async Task<Welcome> AdmitAsync(string ticket)
        {
            await SendAsync(TableWire.Hello(ticket)).ConfigureAwait(false);
            TableWireMessage message = await ReceiveAsync().ConfigureAwait(false);
            if (message.Envelope == null || message.Envelope.BodyCase != Envelope.BodyOneofCase.Welcome)
            {
                throw new InvalidOperationException(
                    "Expected WELCOME after HELLO, got " +
                    (message.Envelope == null
                        ? "close " + message.WsCloseCode + " (" + message.WsCloseReason + ")"
                        : message.Envelope.MessageType.ToString()));
            }

            Welcome = message.Envelope.Welcome;
            return Welcome;
        }

        public Task SendAsync(Envelope envelope)
        {
            return SendAsync(WireCodec.Encode(envelope));
        }

        public async Task SendAsync(byte[] bytes)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Binary,
                true,
                timeout.Token).ConfigureAwait(false);
        }

        /// <summary>Sends one logical message split across several WebSocket frames.</summary>
        public async Task SendFragmentedAsync(byte[] bytes, int fragmentCount)
        {
            if (fragmentCount < 2 || bytes.Length == 0)
            {
                throw new ArgumentException("Fragmenting needs at least two fragments.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int chunkSize = bytes.Length / fragmentCount;
            int offset = 0;
            for (int i = 0; i < fragmentCount; i++)
            {
                bool last = i == fragmentCount - 1;
                int count = last ? bytes.Length - offset : chunkSize;
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes, offset, count),
                    WebSocketMessageType.Binary,
                    last,
                    timeout.Token).ConfigureAwait(false);
                offset += count;
            }
        }

        public async Task SendTextAsync(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                timeout.Token).ConfigureAwait(false);
        }

        public async Task<TableWireMessage> ReceiveAsync(TimeSpan? timeout = null)
        {
            using var cts = TestTimeout.Linked(timeout);
            byte[] buffer = new byte[64 * 1024];
            using var accumulator = new System.IO.MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult received =
                    await _ws.ReceiveAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return TableWireMessage.OfClose(
                        (int)(_ws.CloseStatus ?? 0),
                        _ws.CloseStatusDescription ?? string.Empty);
                }

                if (received.Count > 0)
                {
                    accumulator.Write(buffer, 0, received.Count);
                    if (accumulator.Length > MaxTestInboundBytes)
                    {
                        throw new InvalidOperationException("Test client received an unbounded stream.");
                    }
                }

                if (received.EndOfMessage)
                {
                    byte[] message = accumulator.ToArray();
                    WireDecodeResult decoded = WireCodec.Decode(message);
                    if (!decoded.IsOk || decoded.Envelope == null)
                    {
                        throw new InvalidOperationException(
                            "Test client received an undecodable frame: " + decoded.ErrorCode);
                    }

                    Envelope envelope = decoded.Envelope;
                    if (Environment.GetEnvironmentVariable("R5TRACE") == "1")
                    {
                        Console.WriteLine("R5TRACE type=" + envelope.MessageType +
                            " full=" + (envelope.PlayerUpdate?.IsFullSnapshot ?? false) +
                            " sv=" + (envelope.PlayerUpdate?.Snapshot != null
                                ? envelope.PlayerUpdate.Snapshot.StateVersion.ToString()
                                : (envelope.PublicUpdate?.Snapshot != null
                                    ? envelope.PublicUpdate.Snapshot.StateVersion.ToString()
                                    : "-")) +
                            " cur=" + envelope.PlayerUpdate?.Snapshot.PublicView?.Board.CurrentSeat +
                            " phase=" + envelope.PlayerUpdate?.Snapshot.PublicView?.Board.Phase +
                            " p0river=" + (envelope.PlayerUpdate?.Snapshot.PublicView?.Board.Players.Count > 0
                                ? envelope.PlayerUpdate.Snapshot.PublicView.Board.Players[0].Discards.Count.ToString()
                                : "-") +
                            " pv=" + (envelope.PlayerUpdate?.Snapshot.PrivateView != null).ToString() +
                            " dec=" + (envelope.PlayerUpdate?.Snapshot.PrivateView?.Decision != null).ToString());
                    }
                    if (envelope.PlayerUpdate != null && envelope.PlayerUpdate.IsFullSnapshot)
                    {
                        Interlocked.Exchange(
                            ref _lastBaselineStateVersion,
                            (long)envelope.PlayerUpdate.Snapshot.StateVersion);
                    }
                    else if (envelope.PublicUpdate != null && envelope.PublicUpdate.IsFullSnapshot)
                    {
                        Interlocked.Exchange(
                            ref _lastBaselineStateVersion,
                            (long)envelope.PublicUpdate.Snapshot.StateVersion);
                    }

                    return TableWireMessage.Of(envelope);
                }
            }
        }

        /// <summary>
        /// Reads (skipping heartbeat pings) until the predicate matches; else timeout.
        /// Frames that fail the predicate are RETAINED in a bounded replay buffer and
        /// re-offered to later waits oldest-first — the wire itself is consume-only, so
        /// without the buffer a frame that failed an earlier interleaved predicate would
        /// be lost forever and a later wait on the same stream could starve. A match
        /// consumes the buffer through the matched frame (strict order), so a wait can
        /// never return a frame OLDER than one a previous wait already passed over.
        /// </summary>
        public async Task<Envelope> ReadUntilAsync(
            Func<Envelope, bool> predicate,
            TimeSpan? timeout = null)
        {
            long deadline = Environment.TickCount64 + (long)(timeout ?? TestTimeout.Default).TotalMilliseconds;
            while (true)
            {
                lock (_replayGate)
                {
                    for (int i = 0; i < _replay.Count; i++)
                    {
                        TableWireMessage candidate = _replay[i];
                        if (candidate.IsClose)
                        {
                            throw new InvalidOperationException(
                                "Connection closed while waiting: " +
                                candidate.WsCloseCode + " " + candidate.WsCloseReason);
                        }

                        if (candidate.Envelope!.MessageType == MessageType.Heartbeat &&
                            candidate.Envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                        {
                            _replay.RemoveAt(i);
                            i--;
                            continue;
                        }

                        if (IsStale(candidate))
                        {
                            _replay.RemoveAt(i);
                            i--;
                            continue;
                        }

                        if (predicate(candidate.Envelope))
                        {
                            _replay.RemoveRange(0, i + 1);
                            AdvanceWatermark(candidate);
                            return candidate.Envelope;
                        }
                    }
                }

                TimeSpan remaining = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
                TableWireMessage message = await ReceiveAsync(remaining).ConfigureAwait(false);
                if (message.IsClose)
                {
                    throw new InvalidOperationException(
                        "Connection closed while waiting: " + message.WsCloseCode + " " + message.WsCloseReason);
                }

                // Server keep-alive pings interleave with application traffic and are noise
                // here — but a pong is exactly what the heartbeat tests assert on, so only
                // pings are skipped.
                if (message.Envelope!.MessageType == MessageType.Heartbeat &&
                    message.Envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                {
                    continue;
                }

                if (IsStale(message))
                {
                    continue;
                }

                if (predicate(message.Envelope))
                {
                    AdvanceWatermark(message);
                    return message.Envelope;
                }

                lock (_replayGate)
                {
                    _replay.Add(message);
                    if (_replay.Count > MaxReplayFrames)
                    {
                        _replay.RemoveRange(0, _replay.Count - MaxReplayFrames);
                    }
                }
            }
        }

        /// <summary>Reads until the transport close frame; returns code/reason plus drained envelopes.</summary>
        public async Task<(int WsCloseCode, string Reason, TableWireMessage[] Drained)> ExpectCloseAsync(
            TimeSpan? timeout = null)
        {
            long deadline = Environment.TickCount64 + (long)(timeout ?? TestTimeout.Default).TotalMilliseconds;
            var drained = new System.Collections.Generic.List<TableWireMessage>();
            while (true)
            {
                TimeSpan remaining = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
                TableWireMessage message = await ReceiveAsync(remaining).ConfigureAwait(false);
                if (message.IsClose)
                {
                    return (message.WsCloseCode!.Value, message.WsCloseReason ?? string.Empty, drained.ToArray());
                }

                drained.Add(message);
            }
        }

        /// <summary>Client-initiated clean close (CLOSE envelope then transport close).</summary>
        public async Task CloseCleanAsync()
        {
            await SendAsync(TableWire.CloseEnvelope()).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _ws.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "client-done",
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The server may have closed first; the close decision is still ours.
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _ws.Abort();
                _ws.Dispose();
            }
            catch (Exception)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Frozen-wire envelope builders for the client side of the tests.</summary>
    public static class TableWire
    {
        public static Envelope Hello(
            string ticket,
            uint major = ProtocolConstants.Major,
            uint minor = ProtocolConstants.Minor,
            uint ruleset = ProtocolConstants.RulesetVersion,
            uint projection = ProtocolConstants.ProjectionVersion)
        {
            return new Envelope
            {
                ProtocolMajor = major,
                ProtocolMinor = minor,
                RulesetVersion = ruleset,
                ProjectionVersion = projection,
                MessageType = MessageType.Hello,
                Hello = new Hello
                {
                    Ticket = ticket,
                    ProtocolMajor = major,
                    ProtocolMinor = minor,
                    RulesetVersion = ruleset,
                    ProjectionVersion = projection
                }
            };
        }

        /// <summary>
        /// A HELLO whose version mismatch rides the Hello body — the surface the service
        /// actually negotiates on. Envelope-level fields stay at the frozen constants (the
        /// only thing the codec validates), so the canonical encoder still produces the
        /// exact bytes; a hand-rolled encoding would diverge on proto3 default fields
        /// (e.g. minor=0 is omitted by the canonical writer) and trip strict decode.
        /// </summary>
        public static byte[] RawHello(
            string ticket,
            uint major,
            uint minor = ProtocolConstants.Minor,
            uint ruleset = ProtocolConstants.RulesetVersion,
            uint projection = ProtocolConstants.ProjectionVersion)
        {
            return WireCodec.Encode(new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Hello,
                Hello = new Hello
                {
                    Ticket = ticket,
                    ProtocolMajor = major,
                    ProtocolMinor = minor,
                    RulesetVersion = ruleset,
                    ProjectionVersion = projection
                }
            });
        }

        public static Envelope HeartbeatPing(string nonce)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                Heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } }
            };
        }

        public static Envelope CloseEnvelope()
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Close,
                Close = new Close { Code = ProtocolErrorCode.LocalClosed, WebsocketCloseCode = 1000 }
            };
        }

        /// <summary>A COMMAND envelope that echoes the admitted identity exactly.</summary>
        public static Envelope Command(
            Welcome welcome,
            string requestId,
            Contracts.CommandKind kind,
            byte[] canonicalPayload,
            string? sessionId = null,
            string? roomId = null,
            int? seatId = null,
            ulong? ownerEpoch = null,
            ulong? bindingEpoch = null,
            ulong expectedStateVersion = 0,
            Contracts.Wire.RequestScope? scope = null)
        {
            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Command,
                SessionId = sessionId ?? welcome.SessionId,
                RoomId = roomId ?? welcome.RoomId,
                SeatId = seatId ?? welcome.SeatId,
                BindingEpoch = bindingEpoch ?? welcome.BindingEpoch,
                OwnerEpoch = ownerEpoch ?? welcome.OwnerEpoch,
                RequestId = requestId,
                Command = new Command
                {
                    Type = (Contracts.Wire.CommandType)(int)kind,
                    CanonicalPayload = ByteString.CopyFrom(canonicalPayload),
                    PayloadSchemaVersion = 1,
                    ExpectedStateVersion = expectedStateVersion == 0
                        ? welcome.Baseline.StateVersion
                        : expectedStateVersion
                }
            };
            if (scope != null)
            {
                envelope.Scope = scope;
            }

            return envelope;
        }

        public static Envelope SyncRequest(Welcome welcome, bool playerStream, ulong streamVersion, ulong projectionSeq)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncRequest,
                SessionId = welcome.SessionId,
                RoomId = welcome.RoomId,
                SeatId = welcome.SeatId,
                BindingEpoch = welcome.BindingEpoch,
                OwnerEpoch = welcome.OwnerEpoch,
                SyncRequest = new SyncRequest
                {
                    Target = playerStream ? SyncTarget.PlayerStream : SyncTarget.PublicStream,
                    KnownCursor = new Contracts.Wire.StreamCursor
                    {
                        StreamVersion = streamVersion,
                        ProjectionSeq = projectionSeq
                    }
                }
            };
        }

        public static Envelope ReceiptQuery(Welcome welcome, string scopeId, string requestId)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.ReceiptQuery,
                SessionId = welcome.SessionId,
                RoomId = welcome.RoomId,
                SeatId = welcome.SeatId,
                BindingEpoch = welcome.BindingEpoch,
                OwnerEpoch = welcome.OwnerEpoch,
                ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = scopeId,
                    RequestId = requestId
                }
            };
        }

        /// <summary>MJC1 canonical SUBMIT_ACTION payload (fields 1,2,3,4 strictly ascending).</summary>
        public static byte[] SubmitActionPayload(
            string roundId,
            string decisionId,
            ulong controlEpoch,
            uint actionType,
            uint[]? tileIds = null)
        {
            var writer = new CanonicalPayloadWriter((ushort)Contracts.CommandKind.SubmitAction);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            writer.WriteUInt32(CommandPayloadFields.ActionType, actionType);
            if (tileIds != null)
            {
                writer.WriteRepeatedUInt32(CommandPayloadFields.TileIds, tileIds);
            }

            return writer.Build().CopyBytes();
        }

        public static string RandomNonce()
        {
            return RandomNumberGenerator.GetHexString(16, lowercase: true);
        }

        public static string RandomId(string prefix)
        {
            return prefix + "-" + RandomNumberGenerator.GetHexString(8, lowercase: true);
        }
    }
}
