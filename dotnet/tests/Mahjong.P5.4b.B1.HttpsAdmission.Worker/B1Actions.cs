using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public static class B1Actions
    {
        public static WireDecisionView? FindDecision(PlayerSnapshot? snapshot)
        {
            return snapshot?.PrivateView?.Decision;
        }

        public static WireLegalActionView? FindDiscard(WireDecisionView decision)
        {
            return decision.Actions.FirstOrDefault(
                action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                          action.TileIds.Count > 0);
        }

        public static WireLegalActionView? FindPass(WireDecisionView decision)
        {
            return decision.Actions.FirstOrDefault(
                action => action.ActionType == (uint)ProtocolActionType.Pass);
        }

        public static ProtocolCommand BuildCommand(
            ClientHandle handle,
            PlayerSnapshot snapshot,
            WireDecisionView decision,
            WireLegalActionView action,
            string requestId)
        {
            TableSessionBinding binding = handle.Binding
                ?? throw new InvalidOperationException("The client has no binding.");
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                snapshot.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                (ProtocolActionType)action.ActionType,
                optionId: string.IsNullOrEmpty(action.OptionId) ? null : action.OptionId,
                tileIds: action.TileIds.Count == 0 ? null : action.TileIds.ToArray());
            return new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                binding.Scope.ScopeId,
                new StateVersion(snapshot.StateVersion),
                new RoundId(snapshot.RoundId),
                new DecisionId(decision.DecisionId),
                new ControlEpoch(decision.ControlEpoch),
                binding.OwnerEpoch);
        }

        public static async Task<TableReceiptResolvedEventArgs> SubmitAndWaitAsync(
            ClientHandle handle,
            ProtocolCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CommandSubmission submission = await handle.SubmitAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (!submission.Sent || submission.WasDuplicate)
            {
                throw new InvalidOperationException(
                    "The command was not freshly handed to the real transport (sent=" +
                    submission.Sent + " duplicate=" + submission.WasDuplicate + ").");
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                TableReceiptResolvedEventArgs? resolved = handle.LastResolution;
                if (resolved != null &&
                    string.Equals(
                        resolved.Command.RequestId,
                        command.RequestId.Value,
                        StringComparison.Ordinal))
                {
                    return resolved;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<ReceiptLossChannel.ReceiptObservation> observations = handle.Loss.ObservedReceipts;
            if (observations.Count > 0)
            {
                ReceiptLossChannel.ReceiptObservation observed = observations[observations.Count - 1];
                throw new ClientBlockedException(
                    "CLIENT_RECEIPT_SCOPE_MISSING",
                    "The real TableService delivered a " + observed.Status +
                    " receipt (commandVersion=" + observed.CommandVersion +
                    ", scopePresent=" + observed.ScopePresent +
                    "), but the production Client did not resolve it; inbound=" +
                    handle.Loss.InboundTrace + ".");
            }

            throw new TimeoutException(
                "No definitive receipt for request " + command.RequestId.Value +
                " (state=" + handle.State + ", lastError=" + Program.Redact(handle.LastError) +
                ", inbound=" + handle.Loss.InboundTrace + ").");
        }

        public sealed class ClientBlockedException : Exception
        {
            public ClientBlockedException(string code, string message) : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        /// <summary>
        /// Drives one connected seat until it receives a real DiscardTile offer.
        /// Response-window Pass offers are legal actions too; passing them is how
        /// the seat reaches its draw. Only the final discard is returned.
        /// </summary>
        public static async Task<(PlayerSnapshot Snapshot, WireDecisionView Decision, WireLegalActionView Discard)> WaitForDiscardAsync(
            ClientHandle handle,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            int passes = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                PlayerSnapshot? snapshot = handle.LatestPlayer;
                WireDecisionView? decision = FindDecision(snapshot);
                if (snapshot != null && decision != null)
                {
                    WireLegalActionView? discard = FindDiscard(decision);
                    if (discard != null)
                    {
                        return (snapshot, decision, discard);
                    }

                    WireLegalActionView? pass = FindPass(decision);
                    if (pass != null && passes < 6)
                    {
                        passes++;
                        string requestId = "pass-" + Guid.NewGuid().ToString("N");
                        ProtocolCommand command = BuildCommand(handle, snapshot, decision, pass, requestId);
                        await SubmitAndWaitAsync(handle, command, TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Seat " + handle.Seat + " never received a discard decision.");
        }
    }
}
