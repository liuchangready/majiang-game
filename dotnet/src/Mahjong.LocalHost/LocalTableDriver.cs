using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Local AI/deadline driver. All reasoning happens outside the actor over the immutable
    /// per-seat observation; only a completed decision is admitted back for re-validation.
    /// Draws are direct authoritative commands because a draw has no choice.
    /// </summary>
    internal sealed class LocalTableDriver
    {
        private readonly TableActor _actor;
        private readonly LocalTableHostOptions _options;
        private readonly LocalTableStore _store;
        private readonly RequestScopeId[] _scopes;
        private readonly Func<bool> _isActive;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly long[] _controlEpochs = new long[4];
        private readonly bool[] _aiSeats = new bool[4];
        private readonly object _scopeGate = new object();
        private long _requestCounter;
        private string _lastDecisionKey = string.Empty;
        private Task? _task;

        public LocalTableDriver(
            TableActor actor,
            LocalTableHostOptions options,
            LocalTableStore store,
            RequestScopeId[] scopes,
            Func<bool> isActive)
        {
            _actor = actor;
            _options = options;
            _store = store;
            _scopes = scopes;
            _isActive = isActive;
        }

        public void Start()
        {
            if (_task != null)
            {
                return;
            }

            _task = Task.Run(DriveAsync);
        }

        public bool IsRunning => _task != null && !_task.IsCompleted;

        public async Task StopAsync()
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Task? task = _task;
            if (task != null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }
        }

        private async Task DriveAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_isActive())
                {
                    try
                    {
                        await Task.Delay(20, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    TableSnapshot snapshot = await _actor.GetSnapshotAsync(_stop.Token).ConfigureAwait(false);
                    if (snapshot.IsFinished ||
                        snapshot.Lifecycle == TableActorLifecycle.Closed ||
                        snapshot.Lifecycle == TableActorLifecycle.Faulted)
                    {
                        return;
                    }

                    await DriveOneAsync(snapshot).ConfigureAwait(false);
                    await _actor.TickAsync(TableTickRequest.Empty, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                }

                try
                {
                    await Task.Delay(15, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task DriveOneAsync(TableSnapshot snapshot)
        {
            int seat = snapshot.CurrentSeat;
            switch (snapshot.Phase)
            {
                case RoundPhase.Draw:
                case RoundPhase.KanDraw:
                    if (seat != 0)
                    {
                        await SubmitDrawAsync(snapshot, seat).ConfigureAwait(false);
                    }

                    break;
                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                    if (seat == 0)
                    {
                        break;
                    }

                    IReadOnlyList<TileId> hand = _actor.GetSeatHandTileIds(seat);
                    if (hand.Count == 0)
                    {
                        return;
                    }

                    var candidates = new List<LegalActionOption>(hand.Count);
                    for (int i = 0; i < hand.Count; i++)
                    {
                        candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, hand[i]));
                    }

                    bool opened = await OpenDecisionAsync(snapshot, seat, candidates, hand[hand.Count - 1])
                        .ConfigureAwait(false);
                    if (opened)
                    {
                        await CompleteAiDecisionAsync(seat).ConfigureAwait(false);
                    }

                    break;
                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                    for (int responseSeat = 0; responseSeat <= 3; responseSeat++)
                    {
                        bool openedResponse = await OpenDecisionAsync(
                            snapshot,
                            responseSeat,
                            new[] { new LegalActionOption(LegalActionKind.Pass, responseSeat) },
                            null).ConfigureAwait(false);
                        if (openedResponse)
                        {
                            await CompleteAiDecisionAsync(responseSeat).ConfigureAwait(false);
                        }
                    }

                    break;
            }
        }

        private async Task SubmitDrawAsync(TableSnapshot snapshot, int seat)
        {
            RoundCommand command = RoundCommand.Draw(snapshot.RoundId, seat);
            TableOperationResult result = await SubmitAsync(snapshot, seat, command, "AI_DRAW").ConfigureAwait(false);
            if (result.Status == TableOperationStatus.Rejected &&
                result.Detail != null &&
                result.Detail.IndexOf("WallExhausted", StringComparison.Ordinal) >= 0)
            {
                await SubmitExhaustiveDrawAsync(snapshot, seat).ConfigureAwait(false);
            }
        }

        private async Task<bool> OpenDecisionAsync(
            TableSnapshot snapshot,
            int seat,
            IReadOnlyList<LegalActionOption> candidates,
            TileId? drawnTile)
        {
            if (candidates.Count == 0)
            {
                return false;
            }

            if (_actor.GetPendingDecisionId(seat) != null)
            {
                _lastDecisionKey = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                   snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                   snapshot.Phase.ToString();
                return true;
            }

            string key = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.Phase.ToString();
            if (string.Equals(_lastDecisionKey, key, StringComparison.Ordinal))
            {
                return false;
            }

            string decisionId = "local-ai-" +
                                snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                                seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                                snapshot.Phase.ToString();
            var pending = new PendingDecision(
                new DecisionKey(snapshot.RoundId, seat, decisionId),
                ControlEpoch(snapshot, seat),
                candidates,
                drawnTile);
            TableOperationResult opened = await _actor.OpenDecisionAsync(pending, _stop.Token).ConfigureAwait(false);
            if (opened.Status != TableOperationStatus.Configured)
            {
                return false;
            }

            _lastDecisionKey = key;
            return true;
        }

        private async Task CompleteAiDecisionAsync(int seat)
        {
            TableAiObservationResult captured = await _actor.CaptureAiObservationAsync(seat, _stop.Token)
                .ConfigureAwait(false);
            if (!captured.IsCaptured || captured.Observation == null)
            {
                return;
            }

            Mahjong.Ai.AiObservation observation = captured.Observation;
            LegalActionOption? chosen = null;
            if (observation.MyDrawnTileId.HasValue)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Discard &&
                        option.Tile.HasValue &&
                        option.Tile.Value == observation.MyDrawnTileId.Value)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            if (chosen == null && observation.LegalActions.Count > 0)
            {
                chosen = observation.LegalActions[0];
            }

            if (chosen == null)
            {
                return;
            }

            var intent = new Mahjong.Ai.CommandIntent(observation, chosen, "local-ai", 1);
            var decision = new Mahjong.Ai.AiDecision(observation, intent, 1.0d);
            await _actor.CompleteAiAsync(decision, _stop.Token).ConfigureAwait(false);
            _lastDecisionKey = string.Empty;
        }
        private async Task SubmitExhaustiveDrawAsync(TableSnapshot snapshot, int seat)
        {
            string requestId = "local-ai-settle-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                "AI_EXHAUSTIVE_DRAW|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value);
            var intent = new TableSettlementIntent(
                snapshot.RoundId,
                TableSettlementAction.ExhaustiveDraw,
                seat);
            var request = new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                null,
                _actor.RecoveryGeneration,
                _scopes[seat],
                "AI_EXHAUSTIVE_DRAW",
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitSettlementAsync(request, _stop.Token).ConfigureAwait(false);
            if (result.Status != TableOperationStatus.Accepted && result.Status != TableOperationStatus.Replayed)
            {
            }
        }

        private async Task<TableOperationResult> SubmitAsync(TableSnapshot snapshot, int seat, RoundCommand command, string api)
        {
            string requestId = "local-ai-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value +
                "|" + seat + "|" + command.Action);
            var envelope = new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                null,
                _actor.RecoveryGeneration,
                _scopes[seat],
                api,
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitAsync(envelope, _stop.Token).ConfigureAwait(false);
            if (result.Status != TableOperationStatus.Accepted && result.Status != TableOperationStatus.Replayed)
            {
            }

            return result;
        }

        private static long ControlEpoch(TableSnapshot snapshot, int seat)
        {
            return 0L;
        }
    }
}
