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
            StreamCursor? cursor = null,
            TableVisibleEventBatch? visibleEventBatch = null,
            long acceptanceSequence = 0)
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
            // The batch is already immutable and tied to the accepted snapshot;
            // never clone or replace its identity from a later session read.
            VisibleEventBatch = visibleEventBatch ?? TableVisibleEventBatch.Empty;
            AcceptanceSequence = acceptanceSequence;
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

        /// <summary>
        /// Immutable typed batch emitted for exactly this accepted snapshot.
        /// It is <see cref="TableVisibleEventBatch.Empty"/> only for legacy fake
        /// sessions; a real Client always supplies the same-batch identity.
        /// </summary>
        public TableVisibleEventBatch VisibleEventBatch { get; }

        /// <summary>
        /// Monotonic acceptance sequence assigned when the Client published
        /// this snapshot, before any asynchronous owner capture completes.
        /// A lifecycle boundary can therefore fence an already-accepted but
        /// still pending publication, not only one already dispatched.
        /// </summary>
        public long AcceptanceSequence { get; }
    }

    /// <summary>
    /// Immutable authorization identity captured at snapshot acceptance, before
    /// owner capture can complete. It is UI-only projection identity, never a
    /// Domain/server read.
    /// </summary>
    public sealed class SessionAuthority
    {
        public SessionAuthority(
            long acceptanceSequence,
            string sessionId,
            string roomId,
            string tableId,
            int role,
            int seat,
            ulong bindingEpoch,
            ulong connectionEpoch,
            ulong ownerEpoch,
            string scopeId,
            string roundId,
            bool isPrivateAuthorized,
            TableConnectionState state)
        {
            AcceptanceSequence = acceptanceSequence;
            SessionId = sessionId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            TableId = tableId ?? string.Empty;
            Role = role;
            Seat = seat;
            BindingEpoch = bindingEpoch;
            ConnectionEpoch = connectionEpoch;
            OwnerEpoch = ownerEpoch;
            ScopeId = scopeId ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            IsPrivateAuthorized = isPrivateAuthorized;
            State = state;
        }

        public long AcceptanceSequence { get; }
        public string SessionId { get; }
        public string RoomId { get; }
        public string TableId { get; }
        public int Role { get; }
        public int Seat { get; }
        public ulong BindingEpoch { get; }
        public ulong ConnectionEpoch { get; }
        public ulong OwnerEpoch { get; }
        public string ScopeId { get; }
        public string RoundId { get; }
        public bool IsPrivateAuthorized { get; }
        public TableConnectionState State { get; }

        public bool Matches(SessionViewState view)
        {
            SessionAuthority other = FromView(view);
            return State == other.State &&
                   string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
                   string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
                   string.Equals(TableId, other.TableId, StringComparison.Ordinal) &&
                   Role == other.Role &&
                   Seat == other.Seat &&
                   BindingEpoch == other.BindingEpoch &&
                   ConnectionEpoch == other.ConnectionEpoch &&
                   OwnerEpoch == other.OwnerEpoch &&
                   string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal) &&
                   string.Equals(RoundId, other.RoundId, StringComparison.Ordinal) &&
                   IsPrivateAuthorized == other.IsPrivateAuthorized;
        }

        public static SessionAuthority FromView(SessionViewState view)
        {
            SessionBindingView? binding = view.Binding;
            string roundId = view.PlayerSnapshot?.RoundId ??
                             view.PublicSnapshot?.RoundId ??
                             string.Empty;
            int seat = binding?.SeatId ?? -1;
            bool privateAuthorized = view.State == TableConnectionState.Ready &&
                                     binding != null &&
                                     binding.Role == (int)ProtocolRole.Player &&
                                     view.PlayerSnapshot?.PrivateView != null &&
                                     view.PlayerSnapshot.PrivateView.Seat == seat;
            return new SessionAuthority(
                view.AcceptanceSequence,
                binding?.SessionId ?? string.Empty,
                binding?.RoomId ?? string.Empty,
                binding?.TableId ?? string.Empty,
                binding?.Role ?? (int)ProtocolRole.Unspecified,
                seat,
                binding?.BindingEpoch ?? 0,
                binding?.ConnectionEpoch ?? 0,
                binding?.OwnerEpoch ?? 0,
                binding?.ScopeId ?? string.Empty,
                roundId,
                privateAuthorized,
                view.State);
        }

        public static SessionAuthority FromAccepted(
            long acceptanceSequence,
            TableVisibleEventBatch batch,
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot,
            TableConnectionState state)
        {
            int seat = batch.Seat;
            bool privateAuthorized = state == TableConnectionState.Ready &&
                                     batch.Role == ProtocolRole.Player &&
                                     player?.PrivateView != null &&
                                     player.PrivateView.Seat == seat;
            return new SessionAuthority(
                acceptanceSequence,
                batch.SessionId,
                batch.RoomId,
                batch.TableId,
                (int)batch.Role,
                seat,
                batch.BindingEpoch,
                batch.ConnectionEpoch,
                batch.OwnerEpoch,
                batch.ScopeId,
                player?.RoundId ?? publicSnapshot?.RoundId ?? string.Empty,
                privateAuthorized,
                state);
        }
    }

    public interface ISessionView
    {
        SessionViewState Current { get; }
        event EventHandler<SessionViewState>? Changed;
    }

    /// <summary>
    /// Optional lifecycle fence surface. A real SessionCoordinator implements
    /// it; legacy/fake views that do not are unaffected and report no fence.
    /// </summary>
    public interface ISessionViewLifecycleFence
    {
        long AcceptedSequence { get; }

        /// <summary>
        /// Newest identity accepted by the Client, even when its asynchronous
        /// owner capture has not completed. Null for legacy/fake views.
        /// </summary>
        SessionAuthority? AcceptedAuthority { get; }
    }
}
