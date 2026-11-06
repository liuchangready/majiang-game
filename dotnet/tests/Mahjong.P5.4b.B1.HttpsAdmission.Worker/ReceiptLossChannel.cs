using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts.Wire;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// EXPLICIT B1 FAULT-INJECTION POINT.
    ///
    /// This is a test-only decorator around the REAL WSS transport (it never
    /// creates or answers business messages). It is not a fake ITableChannel:
    /// every byte still travels through the production WebSocket channel to the
    /// real TableService. It only (a) records the exact outbound COMMAND bytes so
    /// the worker can resend them verbatim, and (b) optionally drops the inbound
    /// final RECEIPT for one request id, which is the "server committed, reply
    /// lost" point required by P5.4b-B1. Heartbeats, snapshots and all other
    /// envelopes pass through untouched.
    /// </summary>
    public sealed class ReceiptLossChannel : ITableChannel
    {
        private readonly ITableChannel _inner;
        private readonly object _gate = new object();
        private readonly List<ReceiptObservation> _observed = new List<ReceiptObservation>();
        private readonly List<byte[]> _capturedCommands = new List<byte[]>();
        private readonly Queue<string> _inboundTrace = new Queue<string>();
        private TaskCompletionSource<bool> _firstDropped =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _secondDropped =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReceiptLossChannel(ITableChannel inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Only COMMAND/RECEIPT envelopes carrying this request id are touched.</summary>
        public string? TargetRequestId { get; set; }

        /// <summary>When true, target receipts are dropped before the session sees them.</summary>
        public bool DropTargetReceipts { get; set; }

        public bool IsOpen => _inner.IsOpen;

        public IReadOnlyList<ReceiptObservation> ObservedReceipts
        {
            get
            {
                lock (_gate)
                {
                    return _observed.ToArray();
                }
            }
        }

        public IReadOnlyList<byte[]> CapturedCommandFrames
        {
            get
            {
                lock (_gate)
                {
                    var copy = new List<byte[]>(_capturedCommands.Count);
                    foreach (byte[] frame in _capturedCommands)
                    {
                        copy.Add((byte[])frame.Clone());
                    }

                    return copy;
                }
            }
        }

        public int DroppedReceiptCount
        {
            get
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach (ReceiptObservation observation in _observed)
                    {
                        if (observation.Dropped)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public Task FirstDropped => _firstDropped.Task;

        public Task SecondDropped => _secondDropped.Task;

        public string InboundTrace
        {
            get
            {
                lock (_gate)
                {
                    return string.Join(",", _inboundTrace);
                }
            }
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            return _inner.OpenAsync(request, cancellationToken);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            CaptureCommand(wireEnvelope);
            await _inner.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
        }

        private void CaptureCommand(ReadOnlyMemory<byte> wireEnvelope)
        {
            string? target = TargetRequestId;
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            Envelope envelope;
            try
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.Span);
                if (!decoded.IsOk || decoded.Envelope == null)
                {
                    return;
                }

                envelope = decoded.Envelope;
            }
            catch (WireProtocolException)
            {
                return;
            }

            if (envelope.MessageType != MessageType.Command ||
                !string.Equals(envelope.RequestId, target, StringComparison.Ordinal))
            {
                return;
            }

            lock (_gate)
            {
                _capturedCommands.Add(wireEnvelope.ToArray());
            }
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                string? target = TargetRequestId;
                if (string.IsNullOrEmpty(target))
                {
                    return frame;
                }

                Envelope envelope;
                try
                {
                    WireDecodeResult decoded = WireCodec.Decode(frame.Span);
                    if (!decoded.IsOk || decoded.Envelope == null)
                    {
                        return frame;
                    }

                    envelope = decoded.Envelope;
                }
                catch (WireProtocolException)
                {
                    return frame;
                }

                lock (_gate)
                {
                    string trace = envelope.MessageType.ToString();
                    if (envelope.BodyCase == Envelope.BodyOneofCase.Close && envelope.Close != null)
                    {
                        trace += ":" + envelope.Close.Code;
                    }
                    else if (envelope.BodyCase == Envelope.BodyOneofCase.Error && envelope.Error != null)
                    {
                        trace += ":" + envelope.Error.Code + "/" + envelope.Error.DetailCode;
                    }

                    _inboundTrace.Enqueue(trace);
                    while (_inboundTrace.Count > 20)
                    {
                        _inboundTrace.Dequeue();
                    }
                }

                if (envelope.MessageType != MessageType.Receipt ||
                    !string.Equals(envelope.RequestId, target, StringComparison.Ordinal) ||
                    envelope.Receipt == null)
                {
                    return frame;
                }

                var observation = new ReceiptObservation(
                    envelope.Receipt.Status,
                    envelope.Receipt.Replayed,
                    envelope.Receipt.HasCommandVersion ? envelope.Receipt.CommandVersion : (ulong?)null,
                    envelope.Receipt.EvaluatedStateVersion,
                    envelope.Scope != null && !string.IsNullOrEmpty(envelope.Scope.ScopeId));
                bool drop;
                lock (_gate)
                {
                    drop = DropTargetReceipts;
                    observation.Dropped = drop;
                    _observed.Add(observation);
                    if (drop && _observed.Count == 1)
                    {
                        _firstDropped.TrySetResult(true);
                    }
                }

                if (drop)
                {
                    // The server already committed; this reply is deliberately lost
                    // before the production TableSession can resolve the outbox.
                    if (DroppedReceiptCount >= 2)
                    {
                        _secondDropped.TrySetResult(true);
                    }

                    continue;
                }

                return frame;
            }
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            return _inner.CloseAsync(reason, cancellationToken);
        }

        public sealed class ReceiptObservation
        {
            public ReceiptObservation(ReceiptStatus status, bool replayed, ulong? commandVersion, ulong evaluatedStateVersion, bool scopePresent)
            {
                Status = status;
                Replayed = replayed;
                CommandVersion = commandVersion;
                EvaluatedStateVersion = evaluatedStateVersion;
                ScopePresent = scopePresent;
            }

            public ReceiptStatus Status { get; }

            public bool Replayed { get; }

            public ulong? CommandVersion { get; }

            public ulong EvaluatedStateVersion { get; }

            public bool ScopePresent { get; }

            public bool Dropped { get; internal set; }
        }
    }
}
