using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using GamePhase = Mahjong.Contracts.Wire.GamePhase;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
using SleeveBloomView = Mahjong.Contracts.Wire.SleeveBloomView;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Legal, public-command-only privacy scenario used by the C04-D05 matrix.
    ///
    /// <para>
    /// It never edits authority state, never synthesizes a receipt or projection and
    /// never calls a private runtime/actor member. Every value returned here was
    /// observed after a real <see cref="TableSession"/> submitted a real public
    /// command on the caller-supplied channel.
    /// </para>
    /// </summary>
    public static class LocalPrivacyScenario
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Drives seat zero through a legal TileMark and SleeveBloom and returns the
        /// owner-private fee/ledger and sleeve facts. The caller owns the pump loop.
        /// </summary>
        public static async Task<LocalPrivacyScenarioFacts> CaptureSleeveAndFeeAsync(
            TableSession session,
            CancellationToken cancellationToken,
            bool submitChoice = true,
            int seatId = 0)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (seatId < 0 || seatId > 3) throw new ArgumentOutOfRangeException(nameof(seatId));
            await WaitReadyAsync(session, cancellationToken).ConfigureAwait(false);
            await AdvanceToSeatCheatWindowAsync(session, seatId, cancellationToken).ConfigureAwait(false);

            PlayerSnapshot baseline = RequireSnapshot(session, "baseline");
            if (baseline.PrivateView.Seat != seatId)
                throw new InvalidOperationException("The target session is not bound to requested seat " + seatId + ".");
            PrivateCheatView baselineCheat = RequireCheat(baseline, "baseline");
            if (baseline.PrivateView.ClosedTiles.Count < 4)
                throw new InvalidOperationException("The legal scenario needs at least four owner closed tiles.");

            uint[] markTiles = new uint[4];
            for (int i = 0; i < markTiles.Length; i++) markTiles[i] = baseline.PrivateView.ClosedTiles[i];

            PlayerSnapshot afterMark = await SubmitCheatWithRetryAsync(
                session,
                seatId,
                CommandKind.SubmitTileMark,
                snapshot => CommandPayloadFactory.SubmitTileMark(snapshot.RoundId, "privacy-matrix", 0UL, markTiles),
                "privacy-matrix-mark",
                cancellationToken).ConfigureAwait(false);
            PrivateCheatView markCheat = RequireCheat(afterMark, "tile-mark");
            if (markCheat.TileMark == null || markCheat.TileMark.Groups.Count == 0)
                throw new InvalidOperationException("The legal TileMark command did not produce an owner-private mark group.");
            if (markCheat.TileMark.Groups[0].Tiles.Count == 0)
                throw new InvalidOperationException("The owner-private TileMark group has no marked entity.");
            if (markCheat.LedgerEntries.Count <= baselineCheat.LedgerEntries.Count)
                throw new InvalidOperationException("The legal TileMark command did not append an owner-private ledger entry.");
            if (markCheat.CheatPoints >= baselineCheat.CheatPoints)
                throw new InvalidOperationException("The legal TileMark command did not deduct the owner-private fee.");

            await DiscardOnceAsync(session, seatId, cancellationToken).ConfigureAwait(false);
            await AdvanceToSeatCheatWindowAsync(session, seatId, cancellationToken).ConfigureAwait(false);
            PlayerSnapshot bloom = await SubmitCheatWithRetryAsync(
                session,
                seatId,
                CommandKind.SubmitSleeveBloom,
                snapshot => CommandPayloadFactory.SubmitSleeveBloom(snapshot.RoundId, "privacy-matrix", 0UL),
                "privacy-matrix-sleeve",
                cancellationToken).ConfigureAwait(false);
            PrivateCheatView bloomCheat = RequireCheat(bloom, "sleeve-bloom");
            if (bloomCheat.SleeveBloom == null)
                throw new InvalidOperationException("The legal SleeveBloom command did not produce an owner-private offer.");
            SleeveBloomView offer = bloomCheat.SleeveBloom;
            if (string.IsNullOrEmpty(offer.OfferId))
                throw new InvalidOperationException("The owner-private SleeveBloom offer has no offer id.");
            var offerTiles = new List<uint>(3);
            if (offer.HasFirstTileId) offerTiles.Add(offer.FirstTileId);
            if (offer.HasSecondTileId) offerTiles.Add(offer.SecondTileId);
            if (offer.HasReservedTileId) offerTiles.Add(offer.ReservedTileId);
            if (offerTiles.Count == 0)
                throw new InvalidOperationException("The owner-private SleeveBloom offer has no non-empty tile fact.");
            if (bloomCheat.LedgerEntries.Count <= markCheat.LedgerEntries.Count)
                throw new InvalidOperationException("The legal SleeveBloom command did not append an owner-private ledger entry.");
            if (bloomCheat.CheatPoints >= markCheat.CheatPoints)
                throw new InvalidOperationException("The legal SleeveBloom command did not deduct the owner-private fee.");

            PlayerSnapshot choice = bloom;
            if (submitChoice)
            {
                choice = await SubmitCheatAsync(
                    session,
                    CommandKind.SubmitSleeveBloomChoice,
                    CommandPayloadFactory.SubmitSleeveBloomChoice(offer.OfferId, SleeveBloomChoice.First),
                    "privacy-matrix-sleeve-choice",
                    cancellationToken).ConfigureAwait(false);
            }

            return new LocalPrivacyScenarioFacts
            {
                RoundId = baseline.RoundId,
                BaselineStateVersion = baseline.StateVersion,
                TileMarkStateVersion = afterMark.StateVersion,
                SleeveStateVersion = bloom.StateVersion,
                SleeveChoiceStateVersion = choice.StateVersion,
                BaselineCheatPoints = baselineCheat.CheatPoints,
                TileMarkCheatPoints = markCheat.CheatPoints,
                SleeveCheatPoints = bloomCheat.CheatPoints,
                BaselineLedgerEntryCount = baselineCheat.LedgerEntries.Count,
                TileMarkLedgerEntryCount = markCheat.LedgerEntries.Count,
                SleeveLedgerEntryCount = bloomCheat.LedgerEntries.Count,
                MarkedTileCount = markCheat.TileMark.Groups[0].Tiles.Count,
                MarkedTiles = markTiles,
                SleeveOfferId = offer.OfferId,
                SleeveOfferTiles = offerTiles.ToArray(),
                SleeveOfferIsImmediate = offer.HasDeadline == false
            };
        }

        private static async Task WaitReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 32 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("The privacy scenario session faulted before Ready: " +
                        (session.LastError ?? "<no error>"));
                await session.PumpOnceAsync(cancellationToken).ConfigureAwait(false);
            }

            if (session.State != TableConnectionState.Ready)
                throw new InvalidOperationException("The privacy scenario session did not become Ready.");
        }

        private static PlayerSnapshot RequireSnapshot(TableSession session, string label)
        {
            PlayerSnapshot snapshot = session.PlayerSnapshot ??
                throw new InvalidOperationException("No player snapshot is available for " + label + ".");
            if (snapshot.PrivateView == null)
                throw new InvalidOperationException("The player snapshot for " + label + " has no private view.");
            return snapshot;
        }

        private static PrivateCheatView RequireCheat(PlayerSnapshot snapshot, string label)
        {
            PrivateCheatView? cheat = snapshot.PrivateView.Cheat;
            if (cheat == null) throw new InvalidOperationException("No owner-private cheat view is available for " + label + ".");
            return cheat;
        }

        private static async Task DiscardOnceAsync(TableSession session, int seatId, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 120; i++)
            {
                PlayerSnapshot snapshot = RequireSnapshot(session, "discard");
                var board = snapshot.PublicView?.Board;
                if (board != null && board.CurrentSeat == seatId)
                {
                    if (board.Phase == GamePhase.CheatWindow || board.Phase == GamePhase.Discard)
                    {
                        uint tile = snapshot.PrivateView.HasDrawnTileId
                            ? snapshot.PrivateView.DrawnTileId
                            : snapshot.PrivateView.ClosedTiles[0];
                        try
                        {
                            await SubmitActionAsync(session, ProtocolActionType.DiscardTile, new[] { tile }, cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                        catch (InvalidOperationException exception) when (
                            exception.Message.IndexOf("TILE_TRANSITION_REJECTED", StringComparison.Ordinal) >= 0 ||
                            exception.Message.IndexOf("STALE_STATE_VERSION", StringComparison.Ordinal) >= 0)
                        {
                        }
                    }
                    else if (board.Phase == GamePhase.ResponseWindow || board.Phase == GamePhase.ChankanWindow)
                    {
                        await SubmitActionAsync(session, ProtocolActionType.Pass, null, cancellationToken).ConfigureAwait(false);
                    }
                }
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("The target seat could not discard through a legal phase.");
        }

        private static async Task<PlayerSnapshot> AdvanceToSeatCheatWindowAsync(
            TableSession session,
            int seatId,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 300; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot == null)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (snapshot.PublicView?.Board != null &&
                    snapshot.PublicView.Board.Phase == GamePhase.CheatWindow &&
                    snapshot.PublicView.Board.CurrentSeat == seatId)
                {
                    return snapshot;
                }

                if (snapshot.PublicView?.Board != null && snapshot.PublicView.Board.CurrentSeat == seatId)
                {
                    switch (snapshot.PublicView.Board.Phase)
                    {
                        case GamePhase.CheatWindow:
                        case GamePhase.Discard:
                            uint tile = snapshot.PrivateView.HasDrawnTileId
                                ? snapshot.PrivateView.DrawnTileId
                                : snapshot.PrivateView.ClosedTiles[0];
                            await SubmitActionAsync(session, ProtocolActionType.DiscardTile, new[] { tile }, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case GamePhase.ResponseWindow:
                        case GamePhase.ChankanWindow:
                            await SubmitActionAsync(session, ProtocolActionType.Pass, null, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                    }
                }

                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Seat zero did not reach a legal CheatWindow through public commands.");
        }

        private static async Task<PlayerSnapshot> SubmitActionAsync(
            TableSession session,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                PlayerSnapshot before = RequireSnapshot(session, "action");
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    before.RoundId,
                    "local-direct",
                    0UL,
                    action,
                    tileIds: tiles);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("privacy-action-" + Guid.NewGuid().ToString("N")),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(before.StateVersion),
                    new RoundId(before.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(before.PrivateView.Control?.ControlEpoch ?? 0UL));
                try
                {
                    return await SubmitAndWaitAsync(session, command, before, "action", cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception) when (
                    attempt < 3 && exception.Message.IndexOf("STALE_STATE_VERSION", StringComparison.Ordinal) >= 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("The legal action could not be submitted after stale-version retries.");
        }

        private static async Task<PlayerSnapshot> SubmitCheatWithRetryAsync(
            TableSession session,
            int seatId,
            CommandKind kind,
            Func<PlayerSnapshot, BusinessPayload> payloadFactory,
            string requestPrefix,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await AdvanceToSeatCheatWindowAsync(session, seatId, cancellationToken).ConfigureAwait(false);
                PlayerSnapshot snapshot = RequireSnapshot(session, requestPrefix);
                try
                {
                    return await SubmitCheatAsync(
                        session,
                        kind,
                        payloadFactory(snapshot),
                        requestPrefix,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception) when (
                    attempt < 3 && exception.Message.IndexOf("STALE_STATE_VERSION", StringComparison.Ordinal) >= 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("The legal cheat command could not be submitted after stale-version retries.");
        }

        private static async Task<PlayerSnapshot> SubmitCheatAsync(
            TableSession session,
            CommandKind kind,
            BusinessPayload payload,
            string requestPrefix,
            CancellationToken cancellationToken)
        {
            PlayerSnapshot before = RequireSnapshot(session, requestPrefix);
            var command = new ProtocolCommand(
                kind,
                payload,
                new RequestId(requestPrefix + "-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(before.StateVersion),
                new RoundId(before.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            return await SubmitAndWaitAsync(session, command, before, requestPrefix, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<PlayerSnapshot> SubmitAndWaitAsync(
            TableSession session,
            ProtocolCommand command,
            PlayerSnapshot before,
            string label,
            CancellationToken cancellationToken)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => resolved.TrySetResult(args);
            EventHandler<TableSnapshotChangedEventArgs> snapshotHandler = (_, _) =>
            {
                PlayerSnapshot? current = session.PlayerSnapshot;
                if (current != null && current.StateVersion > before.StateVersion) advanced.TrySetResult(true);
            };
            session.ReceiptResolved += receiptHandler;
            session.SnapshotChanged += snapshotHandler;
            try
            {
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
                if (!submission.Sent) throw new InvalidOperationException(label + " was not sent.");
                TableReceiptResolvedEventArgs receipt = await WaitAsync(resolved.Task, cancellationToken).ConfigureAwait(false);
                if (receipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Applied)
                    throw new InvalidOperationException(label + " was rejected: " + receipt.Resolution.ReasonCode);
                await WaitAsync(advanced.Task, cancellationToken).ConfigureAwait(false);
                return RequireSnapshot(session, label);
            }
            finally
            {
                session.ReceiptResolved -= receiptHandler;
                session.SnapshotChanged -= snapshotHandler;
            }
        }

        private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(CommandTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The legal privacy scenario command did not complete within " +
                    CommandTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
            }

            return await task.ConfigureAwait(false);
        }
    }

    /// <summary>Observed owner-private facts from legal public commands.</summary>
    public sealed class LocalPrivacyScenarioFacts
    {
        public string RoundId { get; set; } = string.Empty;
        public ulong BaselineStateVersion { get; set; }
        public ulong TileMarkStateVersion { get; set; }
        public ulong SleeveStateVersion { get; set; }
        public ulong SleeveChoiceStateVersion { get; set; }
        public long BaselineCheatPoints { get; set; }
        public long TileMarkCheatPoints { get; set; }
        public long SleeveCheatPoints { get; set; }
        public int BaselineLedgerEntryCount { get; set; }
        public int TileMarkLedgerEntryCount { get; set; }
        public int SleeveLedgerEntryCount { get; set; }
        public int MarkedTileCount { get; set; }
        public IReadOnlyList<uint> MarkedTiles { get; set; } = Array.Empty<uint>();
        public string SleeveOfferId { get; set; } = string.Empty;
        public IReadOnlyList<uint> SleeveOfferTiles { get; set; } = Array.Empty<uint>();
        public bool SleeveOfferIsImmediate { get; set; }
    }
}
