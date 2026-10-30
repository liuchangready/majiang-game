#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// One immutable visible event copied from an accepted batch. This is
    /// observation data only; it never re-projects or rewrites the Client batch.
    /// </summary>
    public sealed class ObservedVisibleEvent
    {
        internal ObservedVisibleEvent(TableVisibleEvent source)
        {
            EventSeq = source.EventSeq;
            EventOrdinal = source.EventOrdinal;
            CommandVersion = source.CommandVersion;
            Type = source.Type;
            Visibility = source.Visibility;
            Seat = source.Seat;
            OwnerSeat = source.OwnerSeat;
            Payload = source.Payload;
        }

        public ulong EventSeq { get; }
        public uint EventOrdinal { get; }
        public ulong CommandVersion { get; }
        public ProtocolEventType Type { get; }
        public ProtocolEventVisibility Visibility { get; }
        public int Seat { get; }
        public int OwnerSeat { get; }
        public ProjectedEventPayload Payload { get; }

        public string Key => CommandVersion + ":" + EventSeq + ":" + EventOrdinal;
    }

    /// <summary>
    /// Immutable copy of one accepted <see cref="SessionViewState"/> publication.
    /// The batch object itself is already immutable, but this copy makes the
    /// observation list independent of any later state publication.
    /// </summary>
    public sealed class ObservedVisibleEventBatch
    {
        internal ObservedVisibleEventBatch(SessionViewState state)
        {
            AcceptanceSequence = state.AcceptanceSequence;
            Kind = state.VisibleEventBatch.Kind;
            SessionId = state.VisibleEventBatch.SessionId;
            RoomId = state.VisibleEventBatch.RoomId;
            TableId = state.VisibleEventBatch.TableId;
            RoundId = state.VisibleEventBatch.RoundId;
            ScopeId = state.VisibleEventBatch.ScopeId;
            Role = state.VisibleEventBatch.Role;
            Seat = state.VisibleEventBatch.Seat;
            BindingEpoch = state.VisibleEventBatch.BindingEpoch;
            ConnectionEpoch = state.VisibleEventBatch.ConnectionEpoch;
            OwnerEpoch = state.VisibleEventBatch.OwnerEpoch;
            StateVersion = state.VisibleEventBatch.StateVersion;
            EventSeq = state.VisibleEventBatch.EventSeq;
            FullBaseline = state.VisibleEventBatch.IsFullBaseline;

            var events = new List<ObservedVisibleEvent>(state.VisibleEventBatch.Events.Count);
            for (int i = 0; i < state.VisibleEventBatch.Events.Count; i++)
            {
                events.Add(new ObservedVisibleEvent(state.VisibleEventBatch.Events[i]));
            }

            Events = events.AsReadOnly();
        }

        public long AcceptanceSequence { get; }
        public TableVisibleEventBatchKind Kind { get; }
        public string SessionId { get; }
        public string RoomId { get; }
        public string TableId { get; }
        public string RoundId { get; }
        public string ScopeId { get; }
        public ProtocolRole Role { get; }
        public int Seat { get; }
        public ulong BindingEpoch { get; }
        public ulong ConnectionEpoch { get; }
        public ulong OwnerEpoch { get; }
        public ulong StateVersion { get; }
        public ulong EventSeq { get; }
        public bool FullBaseline { get; }
        public bool Incremental => Kind == TableVisibleEventBatchKind.Incremental;
        public IReadOnlyList<ObservedVisibleEvent> Events { get; }

        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("accept=").Append(AcceptanceSequence)
                .Append(" kind=").Append(Kind)
                .Append(" stateVersion=").Append(StateVersion)
                .Append(" eventSeq=").Append(EventSeq)
                .Append(" round=").Append(RoundId)
                .Append(" binding=").Append(BindingEpoch)
                .Append(" connection=").Append(ConnectionEpoch)
                .Append(" owner=").Append(OwnerEpoch)
                .Append(" events=[");
            for (int i = 0; i < Events.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                ObservedVisibleEvent ev = Events[i];
                builder.Append(ev.Type)
                    .Append("#").Append(ev.EventSeq)
                    .Append('.').Append(ev.EventOrdinal)
                    .Append("@cmd").Append(ev.CommandVersion)
                    .Append("/seat").Append(ev.Seat);
            }

            builder.Append(']');
            return builder.ToString();
        }
    }

    public sealed class ConfirmedVisibleActionEvent
    {
        internal ConfirmedVisibleActionEvent(
            ObservedVisibleEventBatch batch,
            ObservedVisibleEvent ev,
            ulong commandVersion,
            ulong receiptEventSeq)
        {
            Batch = batch;
            Event = ev;
            CommandVersion = commandVersion;
            ReceiptEventSeq = receiptEventSeq;
        }

        public ObservedVisibleEventBatch Batch { get; }
        public ObservedVisibleEvent Event { get; }
        public ulong CommandVersion { get; }
        public ulong ReceiptEventSeq { get; }
    }

    /// <summary>
    /// Independent smoke observer for confirmed visible events. It subscribes
    /// to every accepted <see cref="SessionViewState"/> publication before a
    /// command is submitted and keeps an immutable ordered record, so a later
    /// snapshot cannot replace the batch that belonged to the action.
    ///
    /// Confirmation is correlated only by real contract fields: the Applied
    /// receipt's command_version must equal a visible event's command_version,
    /// under the same binding/scope/round identity, with a state version above
    /// the pre-submit baseline. It never treats a receipt alone, a state-version
    /// bump alone, a baseline replay, another seat's AI command, or an old
    /// binding/round as a confirmed action event.
    /// </summary>
    public sealed class ConfirmedVisibleEventObserver : IDisposable
    {
        private readonly ISessionView _view;
        private readonly object _gate = new object();
        private readonly List<ObservedVisibleEventBatch> _batches = new List<ObservedVisibleEventBatch>();
        private readonly Dictionary<string, ObservedVisibleEvent> _distinctEvents =
            new Dictionary<string, ObservedVisibleEvent>(StringComparer.Ordinal);

        private bool _disposed;
        private bool _actionBound;
        private string _requestId = string.Empty;
        private string _requestScopeId = string.Empty;
        private PlayerActionKind _action;
        private string _actionKind = string.Empty;
        private string _sessionId = string.Empty;
        private string _roomId = string.Empty;
        private string _tableId = string.Empty;
        private string _bindingScopeId = string.Empty;
        private string _roundId = string.Empty;
        private int _role;
        private int _seat;
        private ulong _bindingEpoch;
        private ulong _connectionEpoch;
        private ulong _ownerEpoch;
        private ulong _baselineStateVersion;
        private ReceiptObservation? _receipt;

        public ConfirmedVisibleEventObserver(ISessionView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _view.Changed += OnChanged;
        }

        public void BeginAction(
            SessionViewState baseline,
            string requestId,
            string requestScopeId,
            PlayerActionKind action)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (baseline.Binding == null)
            {
                throw new InvalidOperationException("A visible-event observation requires an accepted binding.");
            }

            if (baseline.PlayerSnapshot == null)
            {
                throw new InvalidOperationException("A visible-event observation requires an accepted player snapshot.");
            }

            lock (_gate)
            {
                _actionBound = true;
                _requestId = requestId ?? string.Empty;
                _requestScopeId = requestScopeId ?? string.Empty;
                _action = action;
                _actionKind = action.ToString();
                _sessionId = baseline.Binding.SessionId;
                _roomId = baseline.Binding.RoomId;
                _tableId = baseline.Binding.TableId;
                _bindingScopeId = baseline.Binding.ScopeId;
                _role = baseline.Binding.Role;
                _seat = baseline.Binding.SeatId;
                _bindingEpoch = baseline.Binding.BindingEpoch;
                _connectionEpoch = baseline.Binding.ConnectionEpoch;
                _ownerEpoch = baseline.Binding.OwnerEpoch;
                _roundId = baseline.PlayerSnapshot.RoundId ?? string.Empty;
                _baselineStateVersion = baseline.PlayerSnapshot.StateVersion;
                _receipt = null;
                _distinctEvents.Clear();
            }
        }

        public void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs e)
        {
            RecordReceipt(e);
        }

        public void RecordReceipt(TableReceiptResolvedEventArgs e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            lock (_gate)
            {
                if (!_actionBound ||
                    !string.Equals(e.Command.RequestId, _requestId, StringComparison.Ordinal) ||
                    !string.Equals(e.Command.ScopeId, _requestScopeId, StringComparison.Ordinal))
                {
                    return;
                }

                _receipt = new ReceiptObservation(
                    e.Resolution.Disposition,
                    e.Resolution.ReceiptStatus,
                    e.Resolution.EvaluatedStateVersion.Value,
                    e.Resolution.CommandVersion?.Value,
                    e.Resolution.EventSeq?.Value,
                    e.Resolution.ReasonCode,
                    e.Resolution.FromReceiptLookup);
            }
        }

        public bool HasAppliedReceipt
        {
            get
            {
                lock (_gate)
                {
                    return _receipt != null &&
                           _receipt.Disposition == OutboxDisposition.Applied &&
                           _receipt.CommandVersion.HasValue;
                }
            }
        }

        public ulong? AppliedCommandVersion
        {
            get
            {
                lock (_gate) return _receipt?.CommandVersion;
            }
        }

        public ulong BaselineStateVersion
        {
            get
            {
                lock (_gate) return _baselineStateVersion;
            }
        }

        public string ReceiptSummary
        {
            get
            {
                lock (_gate) return _receipt == null ? "receipt=none" : _receipt.Describe();
            }
        }

        public IReadOnlyList<ObservedVisibleEventBatch> Batches
        {
            get
            {
                lock (_gate) return _batches.ToArray();
            }
        }

        /// <summary>
        /// Returns the first accepted incremental event whose command_version is
        /// exactly the Applied receipt's command_version, whose type and acting
        /// seat match the selected action's contract, under the same
        /// binding/scope/round. A matching command_version alone is not enough.
        /// Null means no confirmed visible event; the caller must distinguish an
        /// applied state-only action by inspecting <see cref="HasAppliedReceipt"/>.
        /// </summary>
        public ConfirmedVisibleActionEvent? TryGetConfirmation()
        {
            lock (_gate)
            {
                if (!_actionBound ||
                    _receipt == null ||
                    _receipt.Disposition != OutboxDisposition.Applied ||
                    !_receipt.CommandVersion.HasValue)
                {
                    return null;
                }

                // Pass has no own playable event. Window/settlement events that
                // share its command_version must never confirm the Pass.
                if (SmokeActionEventPolicy.AllowsEmptyPublication(_action))
                {
                    return null;
                }

                ulong commandVersion = _receipt.CommandVersion.Value;
                for (int i = 0; i < _batches.Count; i++)
                {
                    ObservedVisibleEventBatch batch = _batches[i];
                    if (!batch.Incremental ||
                        batch.StateVersion <= _baselineStateVersion ||
                        !MatchesIdentity(batch) ||
                        !string.Equals(batch.RoundId, _roundId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (int j = 0; j < batch.Events.Count; j++)
                    {
                        ObservedVisibleEvent ev = batch.Events[j];
                        if (ev.CommandVersion == commandVersion &&
                            SmokeActionEventPolicy.IsRequiredEvent(_action, _seat, ev))
                        {
                            return new ConfirmedVisibleActionEvent(
                                batch,
                                ev,
                                commandVersion,
                                _receipt.EventSeq ?? 0UL);
                        }
                    }
                }

                return null;
            }
        }

        public ObservedVisibleEventBatch? FindBatch(ulong stateVersion)
        {
            lock (_gate)
            {
                for (int i = 0; i < _batches.Count; i++)
                {
                    if (_batches[i].StateVersion == stateVersion)
                    {
                        return _batches[i];
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// First accepted incremental publication whose authoritative state
        /// version equals the Applied receipt's command_version, under the same
        /// binding/scope/round. This is the action's own publication; a later
        /// same-version refresh is not the action batch. Null means the action
        /// publication has not been observed (or was never published), which is
        /// a loss, not a state-only action.
        /// </summary>
        public ObservedVisibleEventBatch? FindActionPublication(ulong commandVersion)
        {
            lock (_gate)
            {
                for (int i = 0; i < _batches.Count; i++)
                {
                    ObservedVisibleEventBatch batch = _batches[i];
                    if (batch.Incremental &&
                        batch.StateVersion == commandVersion &&
                        MatchesIdentity(batch) &&
                        string.Equals(batch.RoundId, _roundId, StringComparison.Ordinal))
                    {
                        return batch;
                    }
                }

                return null;
            }
        }

        public string DescribeBatches()
        {
            lock (_gate)
            {
                if (_batches.Count == 0)
                {
                    return "batches=0";
                }

                var builder = new StringBuilder();
                builder.Append("batches=").Append(_batches.Count);
                for (int i = 0; i < _batches.Count; i++)
                {
                    builder.Append(" | ").Append(_batches[i].Describe());
                }

                return builder.ToString();
            }
        }

        public string DescribeAction()
        {
            lock (_gate)
            {
                return "request=" + MaskRequest(_requestId) +
                       " scope=" + _requestScopeId +
                       " kind=" + _actionKind +
                       " round=" + _roundId +
                       " baseline=" + _baselineStateVersion +
                       " binding=" + _bindingEpoch +
                       " connection=" + _connectionEpoch +
                       " owner=" + _ownerEpoch +
                       " seat=" + _seat;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _view.Changed -= OnChanged;
        }

        private void OnChanged(object? sender, SessionViewState state)
        {
            if (state == null || state.VisibleEventBatch.Kind == TableVisibleEventBatchKind.None)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var observed = new ObservedVisibleEventBatch(state);
                _batches.Add(observed);
                for (int i = 0; i < observed.Events.Count; i++)
                {
                    ObservedVisibleEvent ev = observed.Events[i];
                    if (!_distinctEvents.ContainsKey(ev.Key))
                    {
                        _distinctEvents.Add(ev.Key, ev);
                    }
                }
            }
        }

        private bool MatchesIdentity(ObservedVisibleEventBatch batch)
        {
            return string.Equals(batch.SessionId, _sessionId, StringComparison.Ordinal) &&
                   string.Equals(batch.RoomId, _roomId, StringComparison.Ordinal) &&
                   string.Equals(batch.TableId, _tableId, StringComparison.Ordinal) &&
                   string.Equals(batch.ScopeId, _bindingScopeId, StringComparison.Ordinal) &&
                   batch.Role == (ProtocolRole)_role &&
                   batch.Seat == _seat &&
                   batch.BindingEpoch == _bindingEpoch &&
                   batch.ConnectionEpoch == _connectionEpoch &&
                   batch.OwnerEpoch == _ownerEpoch;
        }

        private static string MaskRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return string.Empty;
            }

            // Smoke request ids are not secrets, but keep evidence logs short and
            // avoid turning an opaque caller id into a persistent identifier.
            return requestId.Length <= 12
                ? requestId
                : requestId.Substring(0, 8) + "..." + requestId.Substring(requestId.Length - 4);
        }

        private sealed class ReceiptObservation
        {
            public ReceiptObservation(
                OutboxDisposition disposition,
                ReceiptStatus receiptStatus,
                ulong evaluatedStateVersion,
                ulong? commandVersion,
                ulong? eventSeq,
                string reasonCode,
                bool fromLookup)
            {
                Disposition = disposition;
                ReceiptStatus = receiptStatus;
                EvaluatedStateVersion = evaluatedStateVersion;
                CommandVersion = commandVersion;
                EventSeq = eventSeq;
                ReasonCode = reasonCode ?? string.Empty;
                FromLookup = fromLookup;
            }

            public OutboxDisposition Disposition { get; }
            public ReceiptStatus ReceiptStatus { get; }
            public ulong EvaluatedStateVersion { get; }
            public ulong? CommandVersion { get; }
            public ulong? EventSeq { get; }
            public string ReasonCode { get; }
            public bool FromLookup { get; }

            public string Describe()
            {
                return "receipt=disposition:" + Disposition +
                       " status:" + ReceiptStatus +
                       " evaluated:" + EvaluatedStateVersion +
                       " command:" + (CommandVersion.HasValue ? CommandVersion.Value.ToString() : "none") +
                       " eventSeq:" + (EventSeq.HasValue ? EventSeq.Value.ToString() : "none") +
                       " reason:" + ReasonCode +
                       " lookup:" + FromLookup;
            }
        }
    }
}
