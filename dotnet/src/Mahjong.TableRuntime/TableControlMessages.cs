using System;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    internal sealed class DisconnectMailboxMessage : TableMailboxMessage
    {
        public DisconnectMailboxMessage(int seat, OwnerEpoch ownerEpoch, ControlEpoch expectedControlEpoch, string reason)
        {
            Seat = seat;
            OwnerEpoch = ownerEpoch;
            ExpectedControlEpoch = expectedControlEpoch;
            Reason = reason ?? string.Empty;
        }

        public int Seat { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ExpectedControlEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ReconnectMailboxMessage : TableMailboxMessage
    {
        public ReconnectMailboxMessage(int seat, OwnerEpoch ownerEpoch, ControlEpoch expectedControlEpoch, string reason)
        {
            Seat = seat;
            OwnerEpoch = ownerEpoch;
            ExpectedControlEpoch = expectedControlEpoch;
            Reason = reason ?? string.Empty;
        }

        public int Seat { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ExpectedControlEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class GraceExpiryMailboxMessage : TableMailboxMessage
    {
        public GraceExpiryMailboxMessage(TableGraceExpiryFence fence)
        {
            Fence = fence ?? throw new ArgumentNullException(nameof(fence));
        }

        public TableGraceExpiryFence Fence { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class TakeoverMailboxMessage : TableMailboxMessage
    {
        public TakeoverMailboxMessage(int seat, OwnerEpoch ownerEpoch, ControlEpoch expectedControlEpoch, string reason)
        {
            Seat = seat;
            OwnerEpoch = ownerEpoch;
            ExpectedControlEpoch = expectedControlEpoch;
            Reason = reason ?? string.Empty;
        }

        public int Seat { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ExpectedControlEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ReclaimMailboxMessage : TableMailboxMessage
    {
        public ReclaimMailboxMessage(int seat, OwnerEpoch ownerEpoch, ControlEpoch expectedControlEpoch, string reason)
        {
            Seat = seat;
            OwnerEpoch = ownerEpoch;
            ExpectedControlEpoch = expectedControlEpoch;
            Reason = reason ?? string.Empty;
        }

        public int Seat { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ExpectedControlEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ConfigurePolicyMailboxMessage : TableMailboxMessage
    {
        public ConfigurePolicyMailboxMessage(DisconnectPolicy policy, OwnerEpoch ownerEpoch, string reason)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            OwnerEpoch = ownerEpoch;
            Reason = reason ?? string.Empty;
        }

        public DisconnectPolicy Policy { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableControlResult> Completion { get; } =
            new TaskCompletionSource<TableControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class AiObservationMailboxMessage : TableMailboxMessage
    {
        public AiObservationMailboxMessage(int seat)
        {
            Seat = seat;
        }

        public int Seat { get; }

        public TaskCompletionSource<TableAiObservationResult> Completion { get; } =
            new TaskCompletionSource<TableAiObservationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ControlSnapshotMailboxMessage : TableMailboxMessage
    {
        public TaskCompletionSource<TableControlSnapshot> Completion { get; } =
            new TaskCompletionSource<TableControlSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class TimingSnapshotMailboxMessage : TableMailboxMessage
    {
        public TaskCompletionSource<TableTimingSnapshot> Completion { get; } =
            new TaskCompletionSource<TableTimingSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
