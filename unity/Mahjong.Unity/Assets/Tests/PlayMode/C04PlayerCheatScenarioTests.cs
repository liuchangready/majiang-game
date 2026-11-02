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

            // G3-01 is BLOCKED at the accepted Player UI entry: the first mark has no
            // PrivateCheatView.TileMark projection and TableCommandIntentFactory rejects it.
            Assert.IsNull(initial.Own.Cheat.TileMark, "first TileMark must be absent before a group");
            Assert.IsFalse(FindButton("TileMarkButton").gameObject.activeSelf, "TileMark control must be hidden");

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
                v => v.Own?.Cheat?.CheatPoints == 110 &&
                     v.Own.Cheat.Ledger.Count == 1 &&
                     v.Own.Cheat.ImmediateSleeveBloom != null,
                15f);
            Assert.IsTrue(_waitOk, "SleeveBloom offer");
            PlayerSleeveBloomState offer = _presenter.LatestView!.Own!.Cheat.ImmediateSleeveBloom!;
            uint second = offer.SecondTileId!.Value;
            Assert.AreEqual(drawnBefore, offer.FirstTileId);
            Assert.AreNotEqual(drawnBefore, second);
            Assert.AreEqual(-10, _presenter.LatestView.Own.Cheat.Ledger[0].Amount);
            Assert.AreEqual(110, _presenter.LatestView.Own.Cheat.Ledger[0].BalanceAfter);

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
            Assert.AreEqual(110, bloomed.Own.Cheat!.CheatPoints);
            Assert.AreEqual(1, bloomed.Own.Cheat.Ledger.Count, "choice must not charge again");

            uint discardTile = bloomed.Own.DrawnTileId ?? bloomed.Own.ClosedTiles[0];
            yield return DiscardEntity(discardTile);
            Assert.IsTrue(_waitOk, "second discard");
            yield return AdvanceToNextDealerTurn();
            Assert.IsTrue(_waitOk, "third dealer turn");

            // BeamSwap activation (10) and a real SWAP.
            _receiptMark = _receipts.Count;
            Click("BeamSwapButton");
            yield return WaitReceipt(CommandKind.SubmitBeamSwap, 15f);
            Assert.IsTrue(_waitOk, "BeamSwap receipt");
            Assert.AreEqual("APPLIED", _lastReceipt!.Status, _lastReceipt.Reason);
            yield return WaitView(
                v => v.Own?.Cheat?.CheatPoints == 100 &&
                     v.Own.Cheat.Ledger.Count == 2 &&
                     v.Own.Cheat.BeamSwap != null,
                15f);
            Assert.IsTrue(_waitOk, "BeamSwap offer");
            PlayerBeamSwapState beam = _presenter.LatestView!.Own!.Cheat.BeamSwap!;
            Assert.Greater(beam.Candidates.Count, 0);
            Assert.Greater(beam.SelectableHandTileIds.Count, 0);
            Assert.AreEqual(-10, _presenter.LatestView.Own.Cheat.Ledger[1].Amount);
            Assert.AreEqual(100, _presenter.LatestView.Own.Cheat.Ledger[1].BalanceAfter);

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
            Assert.AreEqual(100, final.Own!.Cheat!.CheatPoints);
            Assert.AreEqual(2, final.Own.Cheat.Ledger.Count);
            Assert.AreEqual(100, final.Own.Cheat.Ledger[1].BalanceAfter);
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
            if (before == null)
            {
                yield break;
            }

            ClickHandEntity(tileId);
            yield return null;
            Click("DiscardButton");
            ulong version = before.Identity.StateVersion;
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
                            uint tile = view.Own.DrawnTileId ?? view.Own.ClosedTiles[0];
                            ClickHandEntity(tile);
                            yield return null;
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
