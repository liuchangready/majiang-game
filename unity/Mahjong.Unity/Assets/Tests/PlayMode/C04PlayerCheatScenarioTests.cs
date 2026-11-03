#nullable enable
using System;
using Google.Protobuf;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>
    /// C04-G3 component-level PlayMode proof. It drives the real TablePresenter controls over a
    /// real LocalHost channel composed through C04PlayerCheatChannelFactory. It is intentionally
    /// not a final Windows Player result; the Player probe carries that layer separately.
    /// </summary>
    [TestFixture]
    public sealed class C04PlayerCheatScenarioTests
    {
        private string _root = string.Empty;
        private MainThreadDispatcher? _dispatcher;
        private AppComposition? _composition;
        private SessionCoordinator? _coordinator;
        private TablePresenter? _presenter;
        private C04PlayerCheatChannelFactory? _factory;
        private C04RawFrameCapture? _capture;
        private DispatcherPump? _pump;
        private readonly List<Receipt> _receipts = new List<Receipt>();
        private bool _waitOk;
        private Receipt? _lastReceipt;
        private int _receiptMark;
        private C04PhysicalSnapshot? _snapshot;
        private readonly List<C04RawExportAction> _rawActions = new List<C04RawExportAction>();
        private string _producerSmokeRoot = string.Empty;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            UiThemeProvider.EnsureLineBreakingRules();
            UiInputLifecycle.ResetForTest();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_coordinator != null)
            {
                _coordinator.ReceiptResolved -= OnReceiptResolved;
            }

            if (_presenter != null)
            {
                _presenter.Unbind();
            }

            if (_composition != null)
            {
                Task<ShutdownResult> shutdown = _composition.ShutdownAsync("C04_PLAYMODE_TEARDOWN");
                float deadline = Time.realtimeSinceStartup + 15f;
                while (!shutdown.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
            }

            if (_pump != null)
            {
                UnityEngine.Object.DestroyImmediate(_pump.gameObject);
                _pump = null;
            }

            _presenter = null;
            _coordinator = null;
            _composition = null;
            _factory = null;
            _capture = null;
            _dispatcher = null;

            try
            {
                if (!string.IsNullOrEmpty(_root) && Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
            }
        }

        [UnityTest]
        public IEnumerator AuthorizedThreeCheatsThroughPresenterWithExactPrivateLedger()
        {
            yield return OpenAsync(C04PlayerCheatScenario.AuthorizedThreeCheats);

            yield return WaitView(
                view => view.Identity.IsPrivateAuthorized &&
                        view.Own?.Cheat != null &&
                        view.Own.Decision != null,
                30f);
            Assert.IsTrue(_waitOk, "initial confirmed private cheat view");
            PlayerTableViewState initial = _presenter!.LatestView!;
            Assert.AreEqual(120, initial.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(0, initial.Own.Cheat.Ledger.Count);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "initial authority snapshot");
            Assert.AreEqual(120, _snapshot!.balance);
            Assert.AreEqual(0, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            C04PhysicalSnapshot rawBefore = _snapshot;

            // G3-01 TileMark: 1 point, exactly one private ledger entry, 120 -> 119.
            uint[] markTiles = C04PlayerCheatChannelFactory
                .ProposedTileMarkTiles(C04PlayerCheatScenario.AuthorizedThreeCheats);
            _receiptMark = _receipts.Count;
            Click("TileMarkButton");
            yield return null;
            for (int i = 0; i < markTiles.Length; i++)
            {
                ClickHandEntity(markTiles[i]);
                yield return null;
            }

            Click("ConfirmTileMarkButton");
            yield return WaitReceipt(CommandKind.SubmitTileMark, 15f);
            Assert.IsTrue(_waitOk, "TileMark receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            AssertRawAssociation("tile-mark", _lastReceipt, true);
            Assert.IsTrue(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture!.SentFrom(0),
                    _capture.ReceivedFrom(0),
                    _lastReceipt.RequestId,
                    _lastReceipt.CommandVersion,
                    true,
                    out byte[]? rawCommand,
                    out byte[]? rawReceipt,
                    out byte[]? rawPublication),
                "B producer associates original command/receipt/publication");
            Assert.IsNotNull(rawCommand);
            Assert.IsNotNull(rawReceipt);
            Assert.IsNotNull(rawPublication);
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture.SentFrom(0),
                    _capture.ReceivedFrom(0),
                    "missing-request",
                    0,
                    true,
                    out _,
                    out _,
                    out _),
                "missing original request fails closed");
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    Array.Empty<byte[]>(),
                    _capture.ReceivedFrom(0),
                    _lastReceipt.RequestId,
                    _lastReceipt.CommandVersion,
                    true,
                    out _,
                    out _,
                    out _),
                "missing command fails closed");
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture.SentFrom(0),
                    Array.Empty<byte[]>(),
                    _lastReceipt.RequestId,
                    _lastReceipt.CommandVersion,
                    true,
                    out _,
                    out _,
                    out _),
                "missing receipt fails closed");
            byte[][] withoutPublication = _capture.ReceivedFrom(0).Where(frame =>
            {
                Mahjong.Contracts.Wire.WireDecodeResult decoded =
                    Mahjong.Contracts.Wire.WireCodec.Decode(frame);
                return !(decoded.IsOk && decoded.Envelope != null &&
                         decoded.Envelope.MessageType == Mahjong.Contracts.Wire.MessageType.PlayerUpdate);
            }).ToArray();
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture.SentFrom(0),
                    withoutPublication,
                    _lastReceipt.RequestId,
                    _lastReceipt.CommandVersion,
                    true,
                    out _,
                    out _,
                    out _),
                "missing accepted publication fails closed");
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture.SentFrom(0),
                    _capture.ReceivedFrom(0),
                    _lastReceipt.RequestId,
                    _lastReceipt.CommandVersion + 9999UL,
                    true,
                    out _,
                    out _,
                    out _),
                "wrong command version fails closed");
            yield return WaitView(
                v => v.Own?.Cheat?.CheatPoints == 119 &&
                     v.Own.Cheat.Ledger.Count == 1 &&
                     v.Own.Cheat.TileMark != null,
                15f);
            Assert.IsTrue(_waitOk, "TileMark projection");
            Assert.AreEqual(-1, _presenter.LatestView!.Own!.Cheat!.Ledger[0].Amount);
            Assert.AreEqual(119, _presenter.LatestView.Own.Cheat.Ledger[0].BalanceAfter);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "TileMark authority snapshot");
            Assert.AreEqual(119, _snapshot!.balance);
            Assert.AreEqual(1, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            AddRawAction("tile-mark", _lastReceipt, rawBefore, _snapshot, true);
            rawBefore = _snapshot;

            // Discard and advance to the second dealer turn.
            yield return DiscardEntity(120u);
            Assert.IsTrue(_waitOk, "first discard");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "second dealer turn");
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "SleeveBloom command baseline snapshot");
            rawBefore = _snapshot!;

            // SleeveBloom activation (10) and a real SECOND swap.
            uint drawnBefore = _presenter.LatestView!.Own!.DrawnTileId!.Value;
            _receiptMark = _receipts.Count;
            Click("SleeveBloomButton");
            yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom receipt missing; window=" + _presenter.LatestView!.Own!.Cheat!.WindowId + " offer=" + (_presenter.LatestView.Own.Cheat.ImmediateSleeveBloom != null) + " pending=" + (_presenter.InteractionState.Pending?.Intent.Kind.ToString() ?? "null") + " points=" + _presenter.LatestView.Own.Cheat.CheatPoints);
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            AssertRawAssociation("sleeve-bloom", _lastReceipt, true);
            yield return WaitView(
                v => v.Own?.Cheat?.CheatPoints == 109 &&
                     v.Own.Cheat.Ledger.Count == 2 &&
                     v.Own.Cheat.ImmediateSleeveBloom != null,
                15f);
            Assert.IsTrue(_waitOk, "SleeveBloom offer");
            PlayerSleeveBloomState offer = _presenter.LatestView!.Own!.Cheat.ImmediateSleeveBloom!;
            uint second = offer.SecondTileId!.Value;
            Assert.AreEqual(drawnBefore, offer.FirstTileId);
            Assert.AreNotEqual(drawnBefore, second);
            Assert.AreEqual(-10, _presenter.LatestView.Own.Cheat.Ledger[1].Amount);
            Assert.AreEqual(109, _presenter.LatestView.Own.Cheat.Ledger[1].BalanceAfter);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "SleeveBloom offer authority snapshot");
            Assert.AreEqual(109, _snapshot!.balance);
            Assert.AreEqual(2, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            AddRawAction("sleeve-bloom", _lastReceipt, rawBefore, _snapshot, true);
            rawBefore = _snapshot;
            Dictionary<uint, string> beforeSecond = ParsePositions(_snapshot.audit);
            StringAssert.StartsWith("HAND|0|", beforeSecond[drawnBefore]);
            StringAssert.StartsWith("WALL|", beforeSecond[second]);

            _receiptMark = _receipts.Count;
            Click("SleeveBloomButton");
            yield return null;
            _receiptMark = _receipts.Count;
            Click("SleeveSwapButton");
            yield return WaitReceipt(CommandKind.SubmitSleeveBloomChoice, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom choice receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            AssertRawAssociation("sleeve-second", _lastReceipt, true);
            yield return WaitView(v => v.Own?.Cheat?.ImmediateSleeveBloom == null, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom resolved");
            PlayerTableViewState bloomed = _presenter.LatestView!;
            Assert.AreEqual(second, bloomed.Own!.DrawnTileId);
            Assert.AreEqual(109, bloomed.Own.Cheat!.CheatPoints);
            Assert.AreEqual(2, bloomed.Own.Cheat.Ledger.Count, "choice must not charge again");
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "SECOND authority snapshot");
            Assert.AreEqual(109, _snapshot!.balance);
            Assert.AreEqual(2, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            AddRawAction("sleeve-second", _lastReceipt, rawBefore, _snapshot, true);
            rawBefore = _snapshot;
            Dictionary<uint, string> afterSecond = ParsePositions(_snapshot.audit);
            AssertOnlyExchange(beforeSecond, afterSecond, drawnBefore, second);
            StringAssert.StartsWith("WALL|", afterSecond[drawnBefore]);
            StringAssert.StartsWith("HAND|0|", afterSecond[second]);

            // The confirmed decision still carries the pre-swap candidate set. A real player
            // must discard one of those still-in-hand entities; the swapped-in drawn entity is
            // not a legal candidate for this decision.
            uint discardTile = ConfirmedInHandDiscardCandidate(bloomed, second);
            yield return DiscardEntity(discardTile);
            Assert.IsTrue(_waitOk, "second discard through a confirmed candidate");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "third dealer turn");
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "BeamSwap command baseline snapshot");
            rawBefore = _snapshot!;

            // BeamSwap activation (10) and a real SWAP.
            _receiptMark = _receipts.Count;
            Click("BeamSwapButton");
            yield return WaitReceipt(CommandKind.SubmitBeamSwap, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            AssertRawAssociation("beam-swap", _lastReceipt, true);
            yield return WaitView(
                v => v.Own?.Cheat?.CheatPoints == 99 &&
                     v.Own.Cheat.Ledger.Count == 3 &&
                     v.Own.Cheat.BeamSwap != null,
                15f);
            Assert.IsTrue(_waitOk, "BeamSwap offer");
            PlayerBeamSwapState beam = _presenter.LatestView!.Own!.Cheat.BeamSwap!;
            Assert.Greater(beam.Candidates.Count, 0);
            Assert.Greater(beam.SelectableHandTileIds.Count, 0);
            Assert.AreEqual(-10, _presenter.LatestView.Own.Cheat.Ledger[2].Amount);
            Assert.AreEqual(99, _presenter.LatestView.Own.Cheat.Ledger[2].BalanceAfter);

            PlayerBeamSwapCandidateState targetCandidate = beam.Candidates.First(c => c.TileId.HasValue);
            uint targetIndex = 0;
            for (int i = 0; i < beam.Candidates.Count; i++)
            {
                if (ReferenceEquals(beam.Candidates[i], targetCandidate))
                {
                    targetIndex = (uint)i;
                    break;
                }
            }

            uint targetTile = targetCandidate.TileId!.Value;
            uint handTile = beam.SelectableHandTileIds[0];
            Assert.AreNotEqual(handTile, targetTile);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "BeamSwap offer authority snapshot");
            Assert.AreEqual(99, _snapshot!.balance);
            Assert.AreEqual(3, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            AddRawAction("beam-swap", _lastReceipt, rawBefore, _snapshot, true);
            rawBefore = _snapshot;
            Dictionary<uint, string> beforeSwap = ParsePositions(_snapshot.audit);
            StringAssert.StartsWith("HAND|0|", beforeSwap[handTile]);
            string targetBefore = beforeSwap[targetTile];

            _receiptMark = _receipts.Count;
            Click("BeamSwapButton");
            yield return null;
            Click("BeamSlot_" + targetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return null;
            ClickHandEntity(handTile);
            yield return null;
            _receiptMark = _receipts.Count;
            Click("ConfirmBeamSwapButton");
            yield return WaitReceipt(CommandKind.SubmitBeamSwapChoice, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap choice receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            AssertRawAssociation("beam-swap-choice", _lastReceipt, true);
            yield return WaitView(v => v.Own?.Cheat?.BeamSwap == null, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap resolved");
            PlayerTableViewState final = _presenter.LatestView!;
            Assert.AreEqual(99, final.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(3, final.Own.Cheat.Ledger.Count);
            Assert.AreEqual(99, final.Own.Cheat.Ledger[2].BalanceAfter);

            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "SWAP final authority snapshot");
            Assert.AreEqual(99, _snapshot!.balance);
            Assert.AreEqual(3, _snapshot.ledgerEntryCount);
            Assert136(_snapshot.audit);
            AddRawAction("beam-swap-choice", _lastReceipt, rawBefore, _snapshot, true);
            AssertAssociationRejections();
            ExportProducerSmoke(true);
            Dictionary<uint, string> afterSwap = ParsePositions(_snapshot.audit);
            AssertOnlyExchange(beforeSwap, afterSwap, handTile, targetTile);
            StringAssert.StartsWith("HAND|0|", afterSwap[targetTile]);
            Assert.AreEqual(targetBefore, afterSwap[handTile], "hand entity moves to exact target slot");
        }

        private static uint ConfirmedInHandDiscardCandidate(PlayerTableViewState view, uint preferred)
        {
            PlayerDecisionState decision = view.Own!.Decision!;
            uint fallback = uint.MaxValue;
            bool foundFallback = false;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                PlayerLegalActionState candidate = decision.Candidates[i];
                if (candidate.Action != PlayerActionKind.DiscardTile)
                {
                    continue;
                }

                for (int t = 0; t < candidate.TileIds.Count; t++)
                {
                    uint tile = candidate.TileIds[t];
                    bool inHand = view.Own.DrawnTileId == tile || view.Own.ClosedTiles.Contains(tile);
                    if (!inHand)
                    {
                        continue;
                    }

                    if (tile == preferred)
                    {
                        return tile;
                    }

                    if (!foundFallback)
                    {
                        fallback = tile;
                        foundFallback = true;
                    }
                }
            }

            Assert.IsTrue(foundFallback, "confirmed decision exposes an in-hand discard candidate");
            return fallback;
        }

        [UnityTest]
        public IEnumerator UnauthorizedDecisionReceiptIsRejectedWithoutVisibleDelta()
        {
            yield return OpenAsync(C04PlayerCheatScenario.AuthorizedThreeCheats);
            yield return WaitView(
                view => view.Identity.IsPrivateAuthorized &&
                        view.Own?.Cheat != null &&
                        view.Own.Decision != null,
                30f);
            Assert.IsTrue(_waitOk, "initial confirmed private cheat view");
            PlayerTableViewState before = _presenter!.LatestView!;
            ulong version = before.Identity.StateVersion;
            long points = before.Own!.Cheat!.CheatPoints;
            int entries = before.Own.Cheat.Ledger.Count;

            string requestId = "c04-playmode-unauthorized-" + Guid.NewGuid().ToString("N");
            BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                before.Identity.RoundId,
                "c04-no-such-decision",
                before.Own.Decision!.ControlEpoch,
                C04PlayerCheatChannelFactory
                    .ProposedTileMarkTiles(C04PlayerCheatScenario.AuthorizedThreeCheats));
            var command = new ProtocolCommand(
                CommandKind.SubmitTileMark,
                payload,
                new RequestId(requestId),
                new RequestScopeId(before.Identity.ScopeId),
                new StateVersion(before.Identity.StateVersion),
                new RoundId(before.Identity.RoundId),
                decisionId: new DecisionId("c04-no-such-decision"),
                controlEpoch: new ControlEpoch(before.Own.Decision.ControlEpoch));

            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "authority snapshot before unauthorized attempt");
            C04PhysicalSnapshot beforeAuthority = _snapshot!;
            Assert136(beforeAuthority.audit);

            var submission = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = SubmitAsync(command, submission);
            yield return WaitTask(submission.Task, 15f);
            Assert.IsTrue(_waitOk, "unauthorized submission reached the sink");
            yield return WaitReceiptById(requestId, 15f);
            Assert.IsTrue(_waitOk, "unauthorized receipt");
            Assert.AreEqual("REJECTED", _lastReceipt!.Status, _lastReceipt.Reason);
            Assert.AreEqual("DECISION_MISMATCH", _lastReceipt.Reason);
            Assert.AreEqual(requestId, _lastReceipt.RequestId);
            AssertRawAssociation("unauthorized", _lastReceipt, false);
            Assert.IsFalse(string.IsNullOrEmpty(_lastReceipt.PayloadSha256), "unauthorized original payload digest");

            yield return new WaitForSecondsRealtime(0.25f);
            PlayerTableViewState after = _presenter.LatestView!;
            Assert.AreEqual(version, after.Identity.StateVersion);
            Assert.AreEqual(points, after.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(entries, after.Own.Cheat.Ledger.Count);

            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "authority snapshot after unauthorized rejection");
            C04PhysicalSnapshot afterAuthority = _snapshot!;
            Assert.AreEqual(beforeAuthority.stateVersion, afterAuthority.stateVersion);
            Assert.AreEqual(beforeAuthority.eventSeq, afterAuthority.eventSeq);
            Assert.AreEqual(beforeAuthority.cheatFingerprint, afterAuthority.cheatFingerprint);
            Assert.AreEqual(beforeAuthority.tilesSha256, afterAuthority.tilesSha256);
            Assert.AreEqual(beforeAuthority.audit, afterAuthority.audit);
            Assert.AreEqual(beforeAuthority.ledgerEntryCount, afterAuthority.ledgerEntryCount);
            Assert.AreEqual(beforeAuthority.balance, afterAuthority.balance);
            Assert136(afterAuthority.audit);
        }

        [UnityTest]
        public IEnumerator InsufficientFundsIsRejectedByRealPresenterWithZeroAuthorityDelta()
        {
            yield return OpenAsync(C04PlayerCheatScenario.InsufficientBalance);
            yield return WaitView(
                view => view.Identity.IsPrivateAuthorized &&
                        view.Own?.Cheat != null &&
                        view.Own.Decision != null,
                30f);
            Assert.IsTrue(_waitOk, "initial insufficient-funds scene view");
            Assert.AreEqual(120, _presenter!.LatestView!.Own!.Cheat!.CheatPoints);

            PlayerTableViewState negativeInitial = _presenter.LatestView!;
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "negative initial authority snapshot");
            C04PhysicalSnapshot negativeBefore = _snapshot!;
            string unauthorizedId = "c04-playmode-negative-unauthorized-" + Guid.NewGuid().ToString("N");
            BusinessPayload unauthorizedPayload = CommandPayloadFactory.SubmitTileMark(
                negativeInitial.Identity.RoundId,
                "c04-no-such-decision",
                negativeInitial.Own!.Decision!.ControlEpoch,
                C04PlayerCheatChannelFactory.ProposedTileMarkTiles(
                    C04PlayerCheatScenario.InsufficientBalance));
            var unauthorizedCommand = new ProtocolCommand(
                CommandKind.SubmitTileMark,
                unauthorizedPayload,
                new RequestId(unauthorizedId),
                new RequestScopeId(negativeInitial.Identity.ScopeId),
                new StateVersion(negativeInitial.Identity.StateVersion),
                new RoundId(negativeInitial.Identity.RoundId),
                decisionId: new DecisionId("c04-no-such-decision"),
                controlEpoch: new ControlEpoch(negativeInitial.Own.Decision.ControlEpoch));
            var unauthorizedSubmission = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = SubmitAsync(unauthorizedCommand, unauthorizedSubmission);
            yield return WaitTask(unauthorizedSubmission.Task, 15f);
            Assert.IsTrue(_waitOk, "negative unauthorized submission");
            yield return WaitReceiptById(unauthorizedId, 15f);
            Assert.IsTrue(_waitOk, "negative unauthorized receipt");
            Assert.AreEqual("REJECTED", _lastReceipt!.Status, _lastReceipt.Reason);
            Assert.AreEqual("DECISION_MISMATCH", _lastReceipt.Reason);
            yield return new WaitForSecondsRealtime(0.25f);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "negative unauthorized authority snapshot");
            C04PhysicalSnapshot negativeAfter = _snapshot!;
            Assert.AreEqual(negativeBefore.stateVersion, negativeAfter.stateVersion);
            Assert.AreEqual(negativeBefore.eventSeq, negativeAfter.eventSeq);
            Assert.AreEqual(negativeBefore.cheatFingerprint, negativeAfter.cheatFingerprint);
            Assert.AreEqual(negativeBefore.tilesSha256, negativeAfter.tilesSha256);
            Assert.AreEqual(negativeBefore.audit, negativeAfter.audit);
            Assert.AreEqual(negativeBefore.ledgerEntryCount, negativeAfter.ledgerEntryCount);
            Assert.AreEqual(negativeBefore.balance, negativeAfter.balance);
            AddRawAction("unauthorized", _lastReceipt, negativeBefore, negativeAfter, false);

            yield return DiscardEntity(120u);
            Assert.IsTrue(_waitOk, "initial discard");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "second dealer turn");

            for (int turn = 0; turn < 12; turn++)
            {
                _receiptMark = _receipts.Count;
                Click("SleeveBloomButton");
                yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
                Assert.IsTrue(_waitOk, "SleeveBloom activation #" + turn);
                Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
                long expected = 120 - (10 * (turn + 1));
                yield return WaitView(
                    v => v.Own?.Cheat?.CheatPoints == expected &&
                         v.Own.Cheat.ImmediateSleeveBloom != null,
                    15f);
                Assert.IsTrue(_waitOk, "SleeveBloom offer #" + turn);

                _receiptMark = _receipts.Count;
                Click("SleeveBloomButton");
                yield return null;
                _receiptMark = _receipts.Count;
                Click("SleeveStoreButton");
                yield return WaitReceipt(CommandKind.SubmitSleeveBloomChoice, 15f);
                Assert.IsTrue(_waitOk, "SleeveBloom choice #" + turn);
                Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
                yield return WaitView(v => v.Own?.Cheat?.ImmediateSleeveBloom == null, 15f);
                Assert.IsTrue(_waitOk, "SleeveBloom resolved #" + turn);

                uint tile = _presenter.LatestView!.Own!.DrawnTileId ??
                            _presenter.LatestView.Own.ClosedTiles[0];
                yield return DiscardEntity(tile);
                Assert.IsTrue(_waitOk, "discard #" + turn);
                if (turn + 1 < 12)
                {
                    yield return AdvanceToNextDealerTurn();
                    Assert.IsTrue(_waitOk, "next dealer turn #" + turn);
                }
            }

            yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 0, 15f);
            Assert.IsTrue(_waitOk, "balance 0 after twelve legal activations");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "underfunded dealer turn");

            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "authority snapshot before underfunded attempt");
            C04PhysicalSnapshot before = _snapshot!;
            Assert.AreEqual(0, before.balance);
            Assert.AreEqual(12, before.ledgerEntryCount);
            Assert136(before.audit);

            _receiptMark = _receipts.Count;
            Click("SleeveBloomButton");
            yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
            Assert.IsTrue(_waitOk, "underfunded receipt");
            Assert.AreEqual("REJECTED", _lastReceipt!.Status, _lastReceipt.Reason);
            Assert.AreEqual("CHEAT_INSUFFICIENT_FUNDS", _lastReceipt.Reason);
            AssertRawAssociation("insufficient-funds", _lastReceipt, false);

            yield return new WaitForSecondsRealtime(0.25f);
            yield return CaptureSnapshot();
            Assert.IsTrue(_waitOk, "authority snapshot after underfunded rejection");
            C04PhysicalSnapshot after = _snapshot!;
            Assert.AreEqual(before.stateVersion, after.stateVersion);
            Assert.AreEqual(before.eventSeq, after.eventSeq);
            Assert.AreEqual(before.cheatFingerprint, after.cheatFingerprint);
            Assert.AreEqual(before.tilesSha256, after.tilesSha256);
            Assert.AreEqual(before.audit, after.audit);
            Assert.AreEqual(before.ledgerEntryCount, after.ledgerEntryCount);
            Assert.AreEqual(before.balance, after.balance);
            Assert136(after.audit);
            AddRawAction("insufficient-funds", _lastReceipt, before, after, false);
            ExportProducerSmoke(false);
        }

        // -------------------------------------------------------------- composition

        private IEnumerator OpenAsync(C04PlayerCheatScenario kind)
        {
            _rawActions.Clear();
            _producerSmokeRoot = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "build",
                "c04-b-producer-smoke",
                Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_producerSmokeRoot);
            _root = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "build",
                "c04-playmode",
                Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
            var paths = new UnityAppPaths(_root);
            _capture = new C04RawFrameCapture();
            _factory = new C04PlayerCheatChannelFactory(
                paths,
                kind,
                "c04-playmode",
                capture: _capture);
            _dispatcher = new MainThreadDispatcher(512);
            var pumpGo = new GameObject("C04PlayerCheatDispatcherPump");
            UnityEngine.Object.DontDestroyOnLoad(pumpGo);
            _pump = pumpGo.AddComponent<DispatcherPump>();
            _pump.Dispatcher = _dispatcher;
            _composition = new AppComposition(
                new AppConfig(),
                _dispatcher,
                themeProvider: null,
                channelFactory: _factory,
                persistentDataPath: _root);

            var open = new TaskCompletionSource<SessionCoordinator>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = OpenSessionAsync(open);
            yield return WaitTask(open.Task, 30f);
            Assert.IsTrue(_waitOk, "real local session opened");
            _coordinator = open.Task.Result;
            _coordinator.ReceiptResolved += OnReceiptResolved;

            AsyncOperation load = SceneManager.LoadSceneAsync("Table", LoadSceneMode.Single);
            while (load != null && !load.isDone)
            {
                yield return null;
            }

            yield return null;
            yield return null;
            yield return null;
            _presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(_presenter, "TablePresenter in Table scene");
            _presenter!.Initialize(null, _coordinator, _coordinator);
            yield return null;
            yield return null;
        }

        private async Task OpenSessionAsync(TaskCompletionSource<SessionCoordinator> completion)
        {
            try
            {
                SessionCoordinator coordinator = await _composition!.OpenLocalSessionAsync(CancellationToken.None);
                completion.TrySetResult(coordinator);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task SubmitAsync(
            ProtocolCommand command,
            TaskCompletionSource<CommandSubmission> completion)
        {
            try
            {
                CommandSubmission submission = await _coordinator!.SubmitAsync(command, CancellationToken.None);
                completion.TrySetResult(submission);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs args)
        {
            _receipts.Add(new Receipt
            {
                RequestId = args.Command.RequestId,
                Kind = args.Command.Kind,
                Status = args.Resolution.ReceiptStatus.ToString().ToUpperInvariant(),
                Reason = args.Resolution.ReasonCode ?? string.Empty,
                PayloadSha256 = args.Command.DigestSha256,
                CommandVersion = args.Resolution.CommandVersion?.Value ?? 0
            });
        }

        // -------------------------------------------------------------- UI driving

        private Button FindButton(string name)
        {
            Button[] buttons = _presenter!.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                if (string.Equals(buttons[i].name, name, StringComparison.Ordinal))
                {
                    return buttons[i];
                }
            }

            throw new InvalidOperationException("Table button not found: " + name);
        }

        private void Click(string name)
        {
            Button button = FindButton(name);
            UiInputLifecycle.Instance.NotifySubmit(button.gameObject);
            button.onClick.Invoke();
        }

        private void ClickHandEntity(uint tileId)
        {
            if (_presenter?.LatestView?.Own == null)
            {
                return;
            }

            IReadOnlyList<uint> closed = _presenter.LatestView.Own.ClosedTiles;
            for (int i = 0; i < closed.Count; i++)
            {
                if (closed[i] == tileId)
                {
                    Click("HandTile_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            if (_presenter.LatestView.Own.DrawnTileId == tileId)
            {
                Click("DrawnTile");
            }
        }

        private IEnumerator DiscardEntity(uint tileId)
        {
            _waitOk = false;
            PlayerTableViewState? before = _presenter?.LatestView;
            if (before?.Own?.Decision == null)
            {
                yield break;
            }

            uint selected = ConfirmedInHandDiscardCandidate(before, tileId);
            Debug.Log("C04_TEST_DISCARD requested=" + tileId + " selected=" + selected +
                      " decision=" + before.Own.Decision.DecisionId +
                      " closed=" + string.Join(",", before.Own.ClosedTiles) +
                      " drawn=" + (before.Own.DrawnTileId?.ToString() ?? "none") +
                      " candidates=" + string.Join(";", before.Own.Decision.Candidates
                          .Where(c => c.Action == PlayerActionKind.DiscardTile)
                          .Select(c => string.Join("|", c.TileIds))));
            ClickHandEntity(selected);
            yield return null;
            Button discard = FindButton("DiscardButton");
            AssertSelectedDiscardHighlight(selected, before);
            Debug.Log("C04_TEST_DISCARD selection=" + _presenter!.InteractionState.Selection.Kind +
                      "/" + (_presenter.InteractionState.Selection.Action?.Action.ToString() ?? "none") +
                      " button=" + discard.gameObject.activeSelf + "/" + discard.interactable +
                      " pending=" + (_presenter.InteractionState.Pending?.Intent.Command.RequestId.Value ?? "none"));
            if (!discard.gameObject.activeSelf || !discard.interactable)
            {
                yield break;
            }

            _receiptMark = _receipts.Count;
            Click("DiscardButton");
            ulong version = before.Identity.StateVersion;
            yield return WaitReceipt(CommandKind.SubmitAction, 15f);
            Debug.Log("C04_TEST_DISCARD receipt=" + (_lastReceipt?.Status ?? "none") +
                      "/" + (_lastReceipt?.Reason ?? "none") + " wait=" + _waitOk);
            if (!_waitOk || _lastReceipt == null ||
                !string.Equals(_lastReceipt.Status, "APPLIED", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return WaitView(v => v.Identity.StateVersion > version, 15f);
        }

        private IEnumerator AdvanceToNextDealerTurn()
        {
            _waitOk = false;
            ulong version = _presenter?.LatestView?.Identity.StateVersion ?? 0;
            float deadline = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = _presenter?.LatestView;
                if (view != null && view.Identity.StateVersion > version)
                {
                    if (view.Board.Phase == PlayerGamePhase.CheatWindow &&
                        view.Board.CurrentSeat == 0 &&
                        view.Own?.Decision != null)
                    {
                        _waitOk = true;
                        yield break;
                    }

                    if (view.Own?.Decision != null)
                    {
                        if (view.Board.CurrentSeat == 0 &&
                            (view.Board.Phase == PlayerGamePhase.CheatWindow ||
                             view.Board.Phase == PlayerGamePhase.Discard))
                        {
                            uint requested = view.Own.DrawnTileId ?? view.Own.ClosedTiles[0];
                            uint tile = ConfirmedInHandDiscardCandidate(view, requested);
                            ClickHandEntity(tile);
                            yield return null;
                            Button discard = FindButton("DiscardButton");
                            if (!discard.gameObject.activeSelf || !discard.interactable)
                            {
                                _waitOk = false;
                                yield break;
                            }

                            Click("DiscardButton");
                            version = view.Identity.StateVersion;
                            yield return null;
                            continue;
                        }

                        if ((view.Board.Phase == PlayerGamePhase.ResponseWindow ||
                             view.Board.Phase == PlayerGamePhase.ChankanWindow) &&
                            view.Own.Decision.Candidates.Any(a => a.Action == PlayerActionKind.Pass))
                        {
                            Click("PassButton");
                            version = view.Identity.StateVersion;
                            yield return null;
                            continue;
                        }
                    }
                }

                yield return null;
            }
        }

        // -------------------------------------------------------------- waits

        private IEnumerator WaitView(Func<PlayerTableViewState, bool> predicate, float timeoutSeconds)
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = _presenter?.LatestView;
                if (view != null && predicate(view))
                {
                    _waitOk = true;
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator WaitReceipt(CommandKind kind, float timeoutSeconds)
        {
            _waitOk = false;
            _lastReceipt = null;
            int start = _receiptMark;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                for (int i = start; i < _receipts.Count; i++)
                {
                    if (_receipts[i].Kind == kind)
                    {
                        _lastReceipt = _receipts[i];
                        _waitOk = true;
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private IEnumerator WaitReceiptById(string requestId, float timeoutSeconds)
        {
            _waitOk = false;
            _lastReceipt = null;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                for (int i = _receipts.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_receipts[i].RequestId, requestId, StringComparison.Ordinal))
                    {
                        _lastReceipt = _receipts[i];
                        _waitOk = true;
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private IEnumerator WaitTask(Task task, float timeoutSeconds)
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            _waitOk = task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
            if (!_waitOk)
            {
                Debug.LogWarning("C04_PLAYMODE_TASK_FAILED: " + (task.Exception?.ToString() ?? "timeout"));
            }
        }

        private void AssertAssociationRejections()
        {
            Receipt mark = _receipts.First(r => r.Kind == CommandKind.SubmitTileMark);
            Receipt sleeve = _receipts.First(r => r.Kind == CommandKind.SubmitSleeveBloom);
            byte[][] sent = _capture!.SentFrom(0);
            byte[][] received = _capture.ReceivedFrom(0);

            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    sent, received, mark.RequestId, sleeve.CommandVersion, true, out _, out _, out _),
                "another existing accepted version must not bind the mark request");

            byte[][] rejectedReceipt = CloneFrames(received);
            bool mutatedReceipt = false;
            for (int i = 0; i < rejectedReceipt.Length; i++)
            {
                Mahjong.Contracts.Wire.WireDecodeResult decoded =
                    Mahjong.Contracts.Wire.WireCodec.Decode(rejectedReceipt[i]);
                if (decoded.IsOk && decoded.Envelope != null &&
                    decoded.Envelope.MessageType == Mahjong.Contracts.Wire.MessageType.Receipt &&
                    string.Equals(decoded.Envelope.RequestId, mark.RequestId, StringComparison.Ordinal))
                {
                    Mahjong.Contracts.Wire.Envelope envelope =
                        Mahjong.Contracts.Wire.Envelope.Parser.ParseFrom(rejectedReceipt[i]);
                    envelope.Receipt.Status = Mahjong.Contracts.Wire.ReceiptStatus.Rejected;
                    rejectedReceipt[i] = envelope.ToByteArray();
                    mutatedReceipt = true;
                    break;
                }
            }

            Assert.IsTrue(mutatedReceipt, "mark receipt mutated for negative control");
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    sent, rejectedReceipt, mark.RequestId, mark.CommandVersion, true, out _, out _, out _),
                "positive association must reject a typed REJECTED receipt");

            AssertForeignPublication(sent, received, mark, "session");
            AssertForeignPublication(sent, received, mark, "room");
        }

        private void AssertForeignPublication(byte[][] sent, byte[][] received, Receipt mark, string field)
        {
            byte[][] mutated = CloneFrames(received);
            bool changed = false;
            for (int i = 0; i < mutated.Length; i++)
            {
                Mahjong.Contracts.Wire.WireDecodeResult decoded =
                    Mahjong.Contracts.Wire.WireCodec.Decode(mutated[i]);
                if (!decoded.IsOk || decoded.Envelope == null ||
                    decoded.Envelope.MessageType != Mahjong.Contracts.Wire.MessageType.PlayerUpdate ||
                    decoded.Envelope.PlayerUpdate == null ||
                    !decoded.Envelope.PlayerUpdate.VisibleEvents.Any(
                        ev => ev.CommandVersion == mark.CommandVersion))
                {
                    continue;
                }

                Mahjong.Contracts.Wire.Envelope envelope =
                    Mahjong.Contracts.Wire.Envelope.Parser.ParseFrom(mutated[i]);
                if (field == "session")
                {
                    envelope.SessionId = "foreign-session";
                }
                else
                {
                    envelope.RoomId = "foreign-room";
                }

                mutated[i] = envelope.ToByteArray();
                changed = true;
                break;
            }

            Assert.IsTrue(changed, "publication mutated for " + field);
            Assert.IsFalse(
                C04PlayerCheatProbe.TryAssociateRaw(
                    sent, mutated, mark.RequestId, mark.CommandVersion, true, out _, out _, out _),
                "foreign publication " + field + " must not associate");
        }

        private static byte[][] CloneFrames(byte[][] frames)
        {
            var copy = new byte[frames.Length][];
            for (int i = 0; i < frames.Length; i++)
            {
                copy[i] = (byte[])frames[i].Clone();
            }

            return copy;
        }

        private void AddRawAction(
            string id,
            Receipt receipt,
            C04PhysicalSnapshot before,
            C04PhysicalSnapshot after,
            bool requirePublication)
        {
            Assert.IsTrue(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture!.SentFrom(0),
                    _capture.ReceivedFrom(0),
                    receipt.RequestId,
                    receipt.CommandVersion,
                    requirePublication,
                    out byte[]? command,
                    out byte[]? typedReceipt,
                    out byte[]? publication),
                id + " producer association");
            _rawActions.Add(new C04RawExportAction
            {
                id = id,
                command = command!,
                receipt = typedReceipt!,
                publication = requirePublication ? publication : null,
                beforeSave = before.authoritySave,
                beforeCheat = before.cheatState,
                afterSave = after.authoritySave,
                afterCheat = after.cheatState
            });
        }

        private void ExportProducerSmoke(bool authorized)
        {
            byte[][] received = _capture!.ReceivedFrom(0);
            byte[]? welcome = null;
            for (int i = 0; i < received.Length; i++)
            {
                Mahjong.Contracts.Wire.WireDecodeResult decoded =
                    Mahjong.Contracts.Wire.WireCodec.Decode(received[i]);
                if (decoded.IsOk && decoded.Envelope != null &&
                    decoded.Envelope.MessageType == Mahjong.Contracts.Wire.MessageType.Welcome)
                {
                    welcome = received[i];
                    break;
                }
            }

            Assert.IsNotNull(welcome, "producer welcome frame");
            string packageFile = Path.Combine(
                Application.dataPath,
                "Plugins",
                "Mahjong",
                "Mahjong.LocalHost.dll");
            Assert.IsTrue(File.Exists(packageFile), "producer package file");
            string manifest = C04RawEvidenceExporter.WritePackageManifest(
                _producerSmokeRoot,
                "a234432e57efa790c40987d9720091d93bd270a8",
                packageFile);
            string manifestSha;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                manifestSha = BitConverter
                    .ToString(sha.ComputeHash(File.ReadAllBytes(manifest)))
                    .Replace("-", string.Empty);
            }
            string casePath = C04RawEvidenceExporter.Export(
                _producerSmokeRoot,
                "a234432e57efa790c40987d9720091d93bd270a8",
                manifestSha,
                welcome!,
                _capture.SentFrom(0),
                received,
                _rawActions,
                authorized);
            Debug.Log("C04_B_PRODUCER_SMOKE: " + casePath + " manifest=" + manifest);
        }

        private void AssertRawAssociation(string id, Receipt receipt, bool requirePublication)
        {
            Assert.IsTrue(
                C04PlayerCheatProbe.TryAssociateRaw(
                    _capture!.SentFrom(0),
                    _capture.ReceivedFrom(0),
                    receipt.RequestId,
                    receipt.CommandVersion,
                    requirePublication,
                    out byte[]? command,
                    out byte[]? typedReceipt,
                    out byte[]? publication),
                id + " exact raw association");
            Assert.IsNotNull(command, id + " command");
            Assert.IsNotNull(typedReceipt, id + " receipt");
            if (requirePublication)
            {
                Assert.IsNotNull(publication, id + " publication");
            }
            else
            {
                Assert.IsNull(publication, id + " negative must not claim publication");
            }
        }

        private void AssertSelectedDiscardHighlight(uint tileId, PlayerTableViewState view)
        {
            string buttonName;
            if (view.Own!.DrawnTileId == tileId)
            {
                buttonName = "DrawnTile";
            }
            else
            {
                int index = -1;
                for (int i = 0; i < view.Own.ClosedTiles.Count; i++)
                {
                    if (view.Own.ClosedTiles[i] == tileId)
                    {
                        index = i;
                        break;
                    }
                }

                Assert.GreaterOrEqual(index, 0, "selected discard entity is in hand");
                buttonName = "HandTile_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            TMPro.TextMeshProUGUI text = FindButton(buttonName)
                .GetComponentInChildren<TMPro.TextMeshProUGUI>();
            Assert.IsNotNull(text, "discard tile text for " + buttonName);
            Assert.AreEqual(Color.yellow, text.color, "selected discard tile highlight");
        }

        private IEnumerator CaptureSnapshot()
        {
            _waitOk = false;
            _snapshot = null;
            Task<C04PhysicalSnapshot> task = _factory!.ReadPhysicalSnapshotAsync();
            yield return WaitTask(task, 15f);
            if (task.IsCompleted && !task.IsFaulted && !task.IsCanceled)
            {
                _snapshot = task.Result;
            }
        }

        private static Dictionary<uint, string> ParsePositions(string audit)
        {
            var result = new Dictionary<uint, string>();
            string[] lines = audit.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split('|');
                Assert.AreEqual(5, parts.Length, "physical audit line shape: " + line);
                Assert.AreEqual("tile", parts[0]);
                uint id = uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsFalse(result.ContainsKey(id), "physical audit repeats entity " + id);
                result.Add(id, parts[2] + "|" + parts[3] + "|" + parts[4]);
            }

            return result;
        }

        private static void AssertOnlyExchange(
            Dictionary<uint, string> before,
            Dictionary<uint, string> after,
            uint first,
            uint second)
        {
            Assert.AreEqual(136, before.Count, "pre-exchange entity count");
            Assert.AreEqual(136, after.Count, "post-exchange entity count");
            Assert.AreEqual(before[second], after[first], "first entity takes second entity's exact slot");
            Assert.AreEqual(before[first], after[second], "second entity takes first entity's exact slot");
            foreach (KeyValuePair<uint, string> entry in before)
            {
                if (entry.Key == first || entry.Key == second)
                {
                    continue;
                }

                Assert.AreEqual(entry.Value, after[entry.Key], "unrelated entity " + entry.Key + " must not move");
            }
        }

        private static void Assert136(string audit)
        {
            Dictionary<uint, string> positions = ParsePositions(audit);
            Assert.AreEqual(136, positions.Count, "physical audit entity count");
            for (uint id = 0; id < 136; id++)
            {
                Assert.IsTrue(positions.ContainsKey(id), "physical audit is missing entity " + id);
            }
        }

        private sealed class DispatcherPump : MonoBehaviour
        {
            public MainThreadDispatcher? Dispatcher;

            private void Update()
            {
                Dispatcher?.Update(512, 16.0f);
            }
        }

        private sealed class Receipt
        {
            public string RequestId = string.Empty;
            public CommandKind Kind;
            public string Status = string.Empty;
            public string Reason = string.Empty;
            public string PayloadSha256 = string.Empty;
            public ulong CommandVersion;
        }
    }
}
