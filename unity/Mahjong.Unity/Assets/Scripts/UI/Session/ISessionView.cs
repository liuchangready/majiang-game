#nullable enable
using System;
using System.Collections.Generic;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using StreamCursor = Mahjong.Contracts.StreamCursor;

namespace Mahjong.Unity.UI.Session
{
    public sealed class SessionBindingView
    {
        public SessionBindingView(TableSessionBinding source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SessionId = source.SessionId.Value;
            RoomId = source.RoomId.Value;
            TableId = source.TableId;
            Role = (int)source.Role;
            SeatId = source.SeatId;
            ConnectionEpoch = source.ConnectionEpoch.Value;
            OwnerEpoch = source.OwnerEpoch.Value;
            BindingEpoch = source.BindingEpoch.Value;
            ScopeId = source.Scope.ScopeId.Value;
            AccountId = source.Scope.AccountId;
        }

        public string SessionId { get; }
        public string RoomId { get; }
        public string TableId { get; }
        public int Role { get; }
        public int SeatId { get; }
        public ulong ConnectionEpoch { get; }
        public ulong OwnerEpoch { get; }
        public ulong BindingEpoch { get; }
        public string ScopeId { get; }
        public string AccountId { get; }
    }

    public sealed class SessionViewState
    {
        public SessionViewState(
            long generation,
            TableConnectionState state,
            TableSessionBinding? binding,
            PlayerSnapshot? playerSnapshot,
            PublicSnapshot? publicSnapshot,
            RoomUpdate? roomSnapshot,
            IReadOnlyList<PendingCommandSnapshot>? pendingCommands,
            string? lastError,
            bool isFullBaseline = false,
            StreamCursor? cursor = null)
        {
            Generation = generation;
            State = state;
            Binding = binding == null ? null : new SessionBindingView(binding);

            // Protobuf message properties are mutable. Clone() is a deep clone of
            // the message tree (nested messages and repeated fields included);
            // UI and owner must never share a message instance.
            PlayerSnapshot = playerSnapshot?.Clone();
            PublicSnapshot = publicSnapshot?.Clone();
            RoomSnapshot = roomSnapshot?.Clone();

            if (pendingCommands == null)
            {
                PendingCommands = Array.Empty<PendingCommandSnapshot>();
            }
            else
            {
                var copy = new List<PendingCommandSnapshot>(pendingCommands);
                PendingCommands = copy.AsReadOnly();
            }
            LastError = lastError;
            IsFullBaseline = isFullBaseline;
            Cursor = cursor;
        }

        public long Generation { get; }
        public TableConnectionState State { get; }
        public SessionBindingView? Binding { get; }
        public PlayerSnapshot? PlayerSnapshot { get; }
        public PublicSnapshot? PublicSnapshot { get; }
        public RoomUpdate? RoomSnapshot { get; }
        public IReadOnlyList<PendingCommandSnapshot> PendingCommands { get; }
        public string? LastError { get; }
        public bool IsFullBaseline { get; }
        public StreamCursor? Cursor { get; }
    }

    public interface ISessionView
    {
        SessionViewState Current { get; }
        event EventHandler<SessionViewState>? Changed;
    }
}
