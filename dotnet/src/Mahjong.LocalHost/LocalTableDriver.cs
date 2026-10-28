using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly bool[] _temporaryFuriten;
        private readonly bool[] _riichiFuriten;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly long[] _controlEpochs = new long[4];
        private readonly bool[] _aiSeats = new bool[4];
        private readonly string[] _lastDecisionKeys = new string[4];
        private long _requestCounter;
        private Task? _task;

        public LocalTableDriver(
            TableActor actor,
            LocalTableHostOptions options,
            LocalTableStore store,
            RequestScopeId[] scopes,
            Func<bool> isActive,
            bool[] temporaryFuriten,
            bool[] riichiFuriten)
        {
            _actor = actor;
            _options = options;
            _store = store;
            _scopes = scopes;
            _isActive = isActive;
            _temporaryFuriten = temporaryFuriten;
            _riichiFuriten = riichiFuriten;
            for (int i = 0; i < 4; i++)
            {
                _lastDecisionKeys[i] = string.Empty;
            }
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

                    await _actor.TickAsync(TableTickRequest.Empty, _stop.Token).ConfigureAwait(false);
                    await DriveOneAsync(snapshot).ConfigureAwait(false);
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
            TableControlSnapshot control = await _actor.GetControlSnapshotAsync(_stop.Token).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
            {
                _controlEpochs[i] = control.Seats[i].ControlEpoch;
                _aiSeats[i] = control.Seats[i].Kind == SeatControllerKind.Computer ||
                              control.Seats[i].Kind == SeatControllerKind.Takeover;
            }

            int seat = snapshot.CurrentSeat;
            switch (snapshot.Phase)
            {
                case RoundPhase.Draw:
                case RoundPhase.KanDraw:
                    if (_aiSeats[seat])
                    {
                        RoundStateMachine round = _actor.GetRoundStateForHost();
                        if (round.Tiles.WallRemaining == 0)
                        {
                            await SubmitExhaustiveDrawAsync(snapshot, seat).ConfigureAwait(false);
                        }
                        else
                        {
                            await SubmitDrawAsync(snapshot, seat).ConfigureAwait(false);
                        }
                    }

                    break;

                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                {
                    if (!_aiSeats[seat] && _actor.HasActiveCheatOffer(seat))
                    {
                        break;
                    }

                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    if (round.IsFinished)
                    {
                        return;
                    }

                    if (_actor.GetPendingDecisionId(seat) != null)
                    {
                        if (_aiSeats[seat])
                        {
                            await CompleteAiDecisionAsync(seat).ConfigureAwait(false);
                        }

                        break;
                    }

                    IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                        round,
                        seat,
                        _temporaryFuriten,
                        _riichiFuriten);

                    if (candidates.Count == 0)
                    {
                        // Domain facts yield no candidates; do not invent fallback Discard.
                        return;
                    }

                    TileId? drawnTile = round.Tiles.DrawnTile?.Seat == seat ? round.Tiles.DrawnTile?.Tile : null;
                    bool opened = await OpenDecisionAsync(snapshot, seat, candidates, drawnTile).ConfigureAwait(false);
                    if (opened && _aiSeats[seat])
                    {
                        await CompleteAiDecisionAsync(seat).ConfigureAwait(false);
                    }

                    break;
                }

                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                {
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    RoundResponseWindow? window = round.Window;
                    if (window == null)
                    {
                        break;
                    }

                    for (int i = 0; i < window.EligibleSeats.Count; i++)
                    {
                        int responseSeat = window.EligibleSeats[i];
                        if (window.HasResponded(responseSeat))
                        {
                            continue;
                        }

                        if (_actor.GetPendingDecisionId(responseSeat) != null)
                        {
                            if (_aiSeats[responseSeat])
                            {
                                await CompleteAiDecisionAsync(responseSeat).ConfigureAwait(false);
                            }

                            continue;
                        }

                        IReadOnlyList<LegalActionOption> responseCandidates = LocalCandidateProvider.DeriveResponseCandidates(
                            round,
                            responseSeat,
                            window,
                            _temporaryFuriten,
                            _riichiFuriten);

                        if (responseCandidates.Count == 0)
                        {
                            // Domain facts yield no candidate; do not invent fallback Pass.
                            continue;
                        }

                        bool openedResponse = await OpenDecisionAsync(
                            snapshot,
                            responseSeat,
                            responseCandidates,
                            null).ConfigureAwait(false);

                        if (openedResponse && _aiSeats[responseSeat])
                        {
                            await CompleteAiDecisionAsync(responseSeat).ConfigureAwait(false);
                        }
                    }

                    break;
                }
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
                _lastDecisionKeys[seat] = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                          snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                          snapshot.Phase.ToString();
                return true;
            }

            string key = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.Phase.ToString();
            if (string.Equals(_lastDecisionKeys[seat], key, StringComparison.Ordinal))
            {
                return false;
            }

            string prefix = _aiSeats[seat] ? "local-ai-" : "local-human-";
            string decisionId = prefix +
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

            _lastDecisionKeys[seat] = key;
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

            // Prioritize winning declarations (Ron, Tsumo)
            for (int i = 0; i < observation.LegalActions.Count; i++)
            {
                LegalActionOption option = observation.LegalActions[i];
                if (option.Kind == LegalActionKind.Ron || option.Kind == LegalActionKind.Tsumo)
                {
                    chosen = option;
                    break;
                }
            }

            // Next prioritize AddedKan (加杠)
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Kan &&
                        option.OptionId != null &&
                        option.OptionId.StartsWith("added-kan", StringComparison.Ordinal))
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prioritize Pon (碰) in response windows
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Pon)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prefer discard (prefer tsumogiri discard if available)
            if (chosen == null && observation.MyDrawnTileId.HasValue)
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

            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Discard)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prefer Pass
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Pass)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Fallback to first legal action
            if (chosen == null && observation.LegalActions.Count > 0)
            {
                chosen = observation.LegalActions[0];
            }

            if (chosen == null)
            {
                return;
            }

            string? pendingId = _actor.GetPendingDecisionId(seat);
            DecisionId? decId = pendingId != null ? new DecisionId(pendingId) : (DecisionId?)null;
            RoundStateMachine round = _actor.GetRoundStateForHost();
            TableSnapshot snapshot = await _actor.GetSnapshotAsync(_stop.Token).ConfigureAwait(false);

            if (chosen.Kind == LegalActionKind.Discard && chosen.Tile.HasValue)
            {
                IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForDiscard(
                    round,
                    seat,
                    chosen.Tile.Value,
                    _temporaryFuriten,
                    _riichiFuriten);
                RoundCommand command = RoundCommand.Discard(snapshot.RoundId, seat, chosen.Tile.Value, eligible);
                await SubmitAsync(snapshot, seat, command, "AI_DISCARD", decId).ConfigureAwait(false);
            }
            else if (chosen.Kind == LegalActionKind.Pass)
            {
                RoundCommand command = RoundCommand.Pass(snapshot.RoundId, seat);
                await SubmitAsync(snapshot, seat, command, "AI_PASS", decId).ConfigureAwait(false);
            }
            else if (chosen.Kind == LegalActionKind.Tsumo)
            {
                await SubmitSettlementAsync(snapshot, seat, TableSettlementAction.Tsumo, decId).ConfigureAwait(false);
            }
            else if (chosen.Kind == LegalActionKind.Ron)
            {
                await SubmitSettlementAsync(snapshot, seat, TableSettlementAction.Ron, decId).ConfigureAwait(false);
            }
            else if (chosen.Kind == LegalActionKind.Pon)
            {
                RoundResponseWindow? window = round.Window;
                if (window != null)
                {
                    IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                    var matched = new List<TileId>(2);
                    for (int i = 0; i < hand.Count && matched.Count < 2; i++)
                    {
                        if (hand[i].SameType(window.TargetTile))
                        {
                            matched.Add(hand[i]);
                        }
                    }

                    if (matched.Count == 2)
                    {
                        RoundCommand command = RoundCommand.Pon(
                            snapshot.RoundId,
                            seat,
                            window.TargetTile,
                            matched[0],
                            matched[1]);
                        await SubmitAsync(snapshot, seat, command, "AI_PON", decId).ConfigureAwait(false);
                    }
                }
            }
            else if (chosen.Kind == LegalActionKind.Kan &&
                     chosen.OptionId != null &&
                     chosen.OptionId.StartsWith("added-kan", StringComparison.Ordinal) &&
                     chosen.Tile.HasValue)
            {
                IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForAddedKan(
                    round,
                    seat,
                    chosen.Tile.Value,
                    _temporaryFuriten,
                    _riichiFuriten);
                RoundCommand command = RoundCommand.AddedKan(
                    snapshot.RoundId,
                    seat,
                    chosen.Tile.Value,
                    eligible);
                await SubmitAsync(snapshot, seat, command, "AI_ADDED_KAN", decId).ConfigureAwait(false);
            }
            else
            {
                var intent = new Mahjong.Ai.CommandIntent(observation, chosen, "local-ai", 1);
                var decision = new Mahjong.Ai.AiDecision(observation, intent, 1.0d);
                await _actor.CompleteAiAsync(decision, _stop.Token).ConfigureAwait(false);
            }

            _lastDecisionKeys[seat] = string.Empty;
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

        private async Task<TableOperationResult> SubmitSettlementAsync(
            TableSnapshot snapshot,
            int seat,
            TableSettlementAction settlementAction,
            DecisionId? decisionId)
        {
            string requestId = "local-ai-settle-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string api = settlementAction == TableSettlementAction.Tsumo ? "AI_TSUMO" : "AI_RON";
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value);
            var intent = new TableSettlementIntent(
                snapshot.RoundId,
                settlementAction,
                seat);
            var request = new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                decisionId,
                _actor.RecoveryGeneration,
                _scopes[seat],
                api,
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitSettlementAsync(request, _stop.Token).ConfigureAwait(false);
            return result;
        }

        private async Task<TableOperationResult> SubmitAsync(
            TableSnapshot snapshot,
            int seat,
            RoundCommand command,
            string api,
            DecisionId? decisionId = null)
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
                decisionId,
                _actor.RecoveryGeneration,
                _scopes[seat],
                api,
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitAsync(envelope, _stop.Token).ConfigureAwait(false);
            return result;
        }

        private long ControlEpoch(TableSnapshot snapshot, int seat)
        {
            return _controlEpochs[seat];
        }
    }
}
