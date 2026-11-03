#nullable enable
using System;
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
        private DispatcherPump? _pump;
        private readonly List<Receipt> _receipts = new List<Receipt>();
        private bool _waitOk;
        private Receipt? _lastReceipt;
        private int _receiptMark;

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
            yield return WaitView(
                v => v.Own?.Cheat?.CheatPoints == 119 &&
                     v.Own.Cheat.Ledger.Count == 1 &&
                     v.Own.Cheat.TileMark != null,
                15f);
            Assert.IsTrue(_waitOk, "TileMark projection");
            Assert.AreEqual(-1, _presenter.LatestView!.Own!.Cheat!.Ledger[0].Amount);
            Assert.AreEqual(119, _presenter.LatestView.Own.Cheat.Ledger[0].BalanceAfter);

            // Discard and advance to the second dealer turn.
            yield return DiscardEntity(120u);
            Assert.IsTrue(_waitOk, "first discard");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "second dealer turn");

            // SleeveBloom activation (10) and a real SECOND swap.
            uint drawnBefore = _presenter.LatestView!.Own!.DrawnTileId!.Value;
            _receiptMark = _receipts.Count;
            Click("SleeveBloomButton");
            yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom receipt missing; window=" + _presenter.LatestView!.Own!.Cheat!.WindowId + " offer=" + (_presenter.LatestView.Own.Cheat.ImmediateSleeveBloom != null) + " pending=" + (_presenter.InteractionState.Pending?.Intent.Kind.ToString() ?? "null") + " points=" + _presenter.LatestView.Own.Cheat.CheatPoints);
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
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

            _receiptMark = _receipts.Count;
            Click("SleeveBloomButton");
            yield return null;
            _receiptMark = _receipts.Count;
            Click("SleeveSwapButton");
            yield return WaitReceipt(CommandKind.SubmitSleeveBloomChoice, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom choice receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            yield return WaitView(v => v.Own?.Cheat?.ImmediateSleeveBloom == null, 15f);
            Assert.IsTrue(_waitOk, "SleeveBloom resolved");
            PlayerTableViewState bloomed = _presenter.LatestView!;
            Assert.AreEqual(second, bloomed.Own!.DrawnTileId);
            Assert.AreEqual(109, bloomed.Own.Cheat!.CheatPoints);
            Assert.AreEqual(2, bloomed.Own.Cheat.Ledger.Count, "choice must not charge again");

            // The confirmed decision still carries the pre-swap candidate set. A real player
            // must discard one of those still-in-hand entities; the swapped-in drawn entity is
            // not a legal candidate for this decision.
            uint discardTile = ConfirmedInHandDiscardCandidate(bloomed, second);
            yield return DiscardEntity(discardTile);
            Assert.IsTrue(_waitOk, "second discard through a confirmed candidate");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "third dealer turn");

            // BeamSwap activation (10) and a real SWAP.
            _receiptMark = _receipts.Count;
            Click("BeamSwapButton");
            yield return WaitReceipt(CommandKind.SubmitBeamSwap, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
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

            uint handTile = beam.SelectableHandTileIds[0];
            _receiptMark = _receipts.Count;
            Click("BeamSwapButton");
            yield return null;
            Click("BeamSlot_0");
            yield return null;
            ClickHandEntity(handTile);
            yield return null;
            _receiptMark = _receipts.Count;
            Click("ConfirmBeamSwapButton");
            yield return WaitReceipt(CommandKind.SubmitBeamSwapChoice, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap choice receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            yield return WaitView(v => v.Own?.Cheat?.BeamSwap == null, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap resolved");
            PlayerTableViewState final = _presenter.LatestView!;
            Assert.AreEqual(99, final.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(3, final.Own.Cheat.Ledger.Count);
            Assert.AreEqual(99, final.Own.Cheat.Ledger[2].BalanceAfter);
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

            var submission = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = SubmitAsync(command, submission);
            yield return WaitTask(submission.Task, 15f);
            Assert.IsTrue(_waitOk, "unauthorized submission reached the sink");
            yield return WaitReceiptById(requestId, 15f);
            Assert.IsTrue(_waitOk, "unauthorized receipt");
            Assert.AreEqual("REJECTED", _lastReceipt!.Status, _lastReceipt.Reason);
            Assert.AreEqual("DECISION_MISMATCH", _lastReceipt.Reason);

            yield return new WaitForSecondsRealtime(0.25f);
            PlayerTableViewState after = _presenter.LatestView!;
            Assert.AreEqual(version, after.Identity.StateVersion);
            Assert.AreEqual(points, after.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(entries, after.Own.Cheat.Ledger.Count);
        }

        // -------------------------------------------------------------- composition

        private IEnumerator OpenAsync(C04PlayerCheatScenario kind)
        {
            _root = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "build",
                "c04-playmode",
                Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
            var paths = new UnityAppPaths(_root);
            _factory = new C04PlayerCheatChannelFactory(paths, kind, "c04-playmode");
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
                Reason = args.Resolution.ReasonCode ?? string.Empty
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
        }
    }
}
