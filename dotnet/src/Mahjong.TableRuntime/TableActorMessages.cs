using System;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    internal sealed class CommandMailboxMessage : TableMailboxMessage
    {
        public CommandMailboxMessage(TableCommandEnvelope command)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
        }

        public TableCommandEnvelope Command { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class OpenDecisionMailboxMessage : TableMailboxMessage
    {
        public OpenDecisionMailboxMessage(PendingDecision decision)
        {
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }

        public PendingDecision Decision { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class TickMailboxMessage : TableMailboxMessage
    {
        public TickMailboxMessage(TableTickRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public TableTickRequest Request { get; }

        public TaskCompletionSource<TableTickResult> Completion { get; } =
            new TaskCompletionSource<TableTickResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class AiMailboxMessage : TableMailboxMessage
    {
        public AiMailboxMessage(AiDecision decision)
        {
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }

        public AiDecision Decision { get; }

        public TaskCompletionSource<TableTickResult> Completion { get; } =
            new TaskCompletionSource<TableTickResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class BeginRecoveryMailboxMessage : TableMailboxMessage
    {
        public BeginRecoveryMailboxMessage(string reason)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class CompleteRecoveryMailboxMessage : TableMailboxMessage
    {
        public CompleteRecoveryMailboxMessage(TableRecoveryBaseline baseline)
        {
            Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        }

        public TableRecoveryBaseline Baseline { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class LeaseLostMailboxMessage : TableMailboxMessage
    {
        public LeaseLostMailboxMessage(OwnerEpoch ownerEpoch, string reason)
        {
            OwnerEpoch = ownerEpoch;
            Reason = reason ?? string.Empty;
        }

        public OwnerEpoch OwnerEpoch { get; }

        public string Reason { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class SubscribeStreamMailboxMessage : TableMailboxMessage
    {
        public SubscribeStreamMailboxMessage(TableStreamSubscribeRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public TableStreamSubscribeRequest Request { get; }

        public TaskCompletionSource<TableStreamControlResult> Completion { get; } =
            new TaskCompletionSource<TableStreamControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class UnsubscribeStreamMailboxMessage : TableMailboxMessage
    {
        public UnsubscribeStreamMailboxMessage(TableStreamHandle handle)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        }

        public TableStreamHandle Handle { get; }

        public TaskCompletionSource<TableStreamControlResult> Completion { get; } =
            new TaskCompletionSource<TableStreamControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class SyncStreamMailboxMessage : TableMailboxMessage
    {
        public SyncStreamMailboxMessage(TableStreamSyncRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public TableStreamSyncRequest Request { get; }

        public TaskCompletionSource<TableStreamControlResult> Completion { get; } =
            new TaskCompletionSource<TableStreamControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class SnapshotMailboxMessage : TableMailboxMessage
    {
        public TaskCompletionSource<TableSnapshot> Completion { get; } =
            new TaskCompletionSource<TableSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class CloseMailboxMessage : TableMailboxMessage
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
