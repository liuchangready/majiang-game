#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Opt-in Windows Player probe for the owner seat control path. It never runs
    /// unless the process is started with <c>-local-control-probe &lt;dir&gt;</c>.
    /// It uses the production Menu -> Table route, the real scene Buttons and
    /// the real Client command sink; it does not call LocalTableHost, Domain or
    /// TableRuntime control methods. A control change fences the old seat stream,
    /// so the probe performs the same explicit leave/re-enter reconnect that the
    /// product requires, and proves reconnect does not auto-claim.
    /// </summary>
    public sealed class LocalControlPlayerProbe : MonoBehaviour
    {
        private const string Flag = "-local-control-probe";

        private string _outputDirectory = string.Empty;
        private StringBuilder _log = new StringBuilder();
        private TableReceiptResolvedEventArgs? _lastReceipt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            string? directory = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == Flag && i + 1 < args.Length)
                {
                    directory = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var go = new GameObject("LocalControlPlayerProbe");
            DontDestroyOnLoad(go);
            var probe = go.AddComponent<LocalControlPlayerProbe>();
            probe._outputDirectory = directory!;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(_outputDirectory);
            Record("PROBE_START dir=" + _outputDirectory);
            bool success = false;
            string failure = string.Empty;
            yield return RunProbe(result => { success = result; }, message => failure = message);

            if (success)
            {
                Record("CONTROL_PROBE_PASS");
                Debug.Log("CONTROL_PROBE_PASS");
                Application.Quit(0);
            }
            else
            {
                Record("CONTROL_PROBE_FAIL " + failure);
                Debug.LogError("CONTROL_PROBE_FAIL " + failure);
                Application.Quit(1);
            }
        }

        private IEnumerator RunProbe(Action<bool> complete, Action<string> fail)
        {
            AppRoot? root = null;
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                root = AppRoot.Instance;
                if (root != null && root.Router.CurrentScene == SceneId.Menu)
                {
                    break;
                }

                yield return null;
            }

            if (root == null || root.Router.CurrentScene != SceneId.Menu)
            {
                fail("APP_ROOT_OR_MENU_TIMEOUT");
                complete(false);
                yield break;
            }

            yield return StartLocalGame(root, "initial");
            TablePresenter presenter = RequirePresenter();
            yield return WaitForView(presenter, view => view.Control != null && !view.Control.ControlledByComputer,
                "initial human control", fail);
            if (presenter.LatestView == null || presenter.LatestView.Own?.Decision == null)
            {
                fail("INITIAL_DECISION_MISSING");
                complete(false);
                yield break;
            }

            PlayerTableViewState initial = presenter.LatestView;
            ulong initialEpoch = initial.Control!.ControlEpoch;
            string initialRound = initial.Identity.RoundId;
            string oldDecision = initial.Own!.Decision!.DecisionId;
            ulong oldEpoch = initial.Own.Decision.ControlEpoch;
            Record("INITIAL epoch=" + initialEpoch + " decisionEpoch=" + oldEpoch);

            yield return ClickAndWait(root, presenter, "_releaseControlButton",
                CommandKind.ReleaseControl, "release", fail);
            if (!IsRecorded(_lastReceipt))
            {
                fail("RELEASE_NOT_RECORDED");
                complete(false);
                yield break;
            }

            yield return Reconnect(root, fail);
            presenter = RequirePresenter();
            yield return WaitForView(presenter, view => view.Control != null &&
                                                          view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 1UL,
                "automated control after reconnect", fail);
            yield return ClickAndWait(root, presenter, "_claimControlButton",
                CommandKind.ClaimControl, "claim", fail);
            if (!IsRecorded(_lastReceipt))
            {
                fail("CLAIM_NOT_RECORDED");
                complete(false);
                yield break;
            }

            yield return Reconnect(root, fail);
            presenter = RequirePresenter();
            yield return WaitForView(presenter, view => view.Control != null &&
                                                          !view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 2UL,
                "human control after reclaim", fail);

            yield return WaitForView(presenter, view => view.Own?.Decision != null,
                "decision after reclaim", fail);
            PlayerTableViewState reclaimed = presenter.LatestView!;
            PlayerDecisionState decision = reclaimed.Own!.Decision!;
            PlayerLegalActionState? discard = null;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                if (decision.Candidates[i].Action == PlayerActionKind.DiscardTile)
                {
                    discard = decision.Candidates[i];
                    break;
                }
            }

            if (discard == null)
            {
                fail("NO_DISCARD_CANDIDATE");
                complete(false);
                yield break;
            }

            var stale = new ProtocolCommand(
                CommandKind.SubmitAction,
                CommandPayloadFactory.SubmitAction(
                    reclaimed.Identity.RoundId,
                    decision.DecisionId,
                    oldEpoch,
                    ProtocolActionType.DiscardTile,
                    string.IsNullOrEmpty(discard.OptionId) ? null : discard.OptionId,
                    discard.TileIds),
                new RequestId("probe-stale-" + Guid.NewGuid().ToString("N")),
                new RequestScopeId(reclaimed.Identity.ScopeId),
                new StateVersion(reclaimed.Identity.StateVersion),
                new RoundId(reclaimed.Identity.RoundId),
                new DecisionId(decision.DecisionId),
                new ControlEpoch(oldEpoch),
                new OwnerEpoch(reclaimed.Identity.OwnerEpoch));

            _lastReceipt = null;
            Task<CommandSubmission> submit = root.Composition.SessionCoordinator!.SubmitAsync(
                stale,
                CancellationToken.None);
            while (!submit.IsCompleted) yield return null;
            yield return WaitForReceipt(CommandKind.SubmitAction, "stale action receipt", fail);
            if (_lastReceipt == null ||
                _lastReceipt.Command.LastReceipt != ReceiptStatus.Rejected ||
                _lastReceipt.Resolution.ReasonCode != ProtocolErrorCode.StaleControlEpoch)
            {
                fail("STALE_ACTION_NOT_REJECTED_AS_STALE_CONTROL_EPOCH reason=" +
                     (_lastReceipt?.Resolution.ReasonCode ?? "<none>"));
                complete(false);
                yield break;
            }

            Record("RESULT initialEpoch=" + initialEpoch +
                   " releaseEpoch=" + (initialEpoch + 1UL) +
                   " claimEpoch=" + (initialEpoch + 2UL) +
                   " oldEpoch=" + oldEpoch +
                   " staleReason=" + _lastReceipt.Resolution.ReasonCode +
                   " round=" + initialRound);
            complete(true);
        }

        private IEnumerator StartLocalGame(AppRoot root, string phase)
        {
            Task<LocalGameStartResult> start = root.Router.StartLocalGameAsync();
            while (!start.IsCompleted) yield return null;
            Record("START " + phase + " status=" + start.Result.Status + " code=" + start.Result.StableErrorCode);
            if (start.Result.Status != LocalGameStartStatus.Started)
            {
                throw new InvalidOperationException("LOCAL_START_FAILED " + phase + " " + start.Result.StableErrorCode);
            }

            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline && root.Router.CurrentScene != SceneId.Table)
            {
                yield return null;
            }

            yield return null;
            SessionCoordinator? coordinator = root.Composition.SessionCoordinator;
            if (coordinator == null)
            {
                throw new InvalidOperationException("NO_SESSION_COORDINATOR");
            }

            coordinator.ReceiptResolved -= OnReceiptResolved;
            coordinator.ReceiptResolved += OnReceiptResolved;
            TablePresenter presenter = RequirePresenter();
            presenter.Bind(coordinator, root.Router, coordinator, null);
            yield return WaitForView(presenter, view => view.Identity.IsPrivateAuthorized,
                "baseline " + phase, message => throw new InvalidOperationException(message));
        }

        private IEnumerator Reconnect(AppRoot root, Action<string> fail)
        {
            SessionCoordinator? coordinator = root.Composition.SessionCoordinator;
            if (coordinator != null)
            {
                coordinator.ReceiptResolved -= OnReceiptResolved;
            }

            Task<bool> menu = root.Router.ReturnToMenuAsync();
            while (!menu.IsCompleted) yield return null;
            if (!menu.Result)
            {
                fail("RETURN_TO_MENU_FAILED");
                yield break;
            }

            yield return StartLocalGame(root, "reconnect");
        }

        private IEnumerator ClickAndWait(
            AppRoot root,
            TablePresenter presenter,
            string field,
            CommandKind kind,
            string phase,
            Action<string> fail)
        {
            FieldInfo? info = typeof(TablePresenter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Button? button = info?.GetValue(presenter) as Button;
            if (button == null)
            {
                fail("BUTTON_MISSING " + field);
                yield break;
            }

            if (!button.gameObject.activeInHierarchy || !button.interactable)
            {
                fail("BUTTON_DISABLED " + field);
                yield break;
            }

            _lastReceipt = null;
            UiInputLifecycle.Instance.OnResume();
            UiInputLifecycle.Instance.OnFocusGained();
            button.onClick.Invoke();
            Record("CLICK " + phase + " " + field);
            yield return WaitForReceipt(kind, phase + " receipt", fail);
        }

        private IEnumerator WaitForReceipt(CommandKind kind, string label, Action<string> fail)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (_lastReceipt != null && _lastReceipt.Command.Kind == kind &&
                    _lastReceipt.Command.LastReceipt.HasValue)
                {
                    yield break;
                }

                yield return null;
            }

            fail("RECEIPT_TIMEOUT " + label);
        }

        private static IEnumerator WaitForView(
            TablePresenter presenter,
            Func<PlayerTableViewState, bool> predicate,
            string label,
            Action<string> fail)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = presenter.LatestView;
                if (view != null && predicate(view))
                {
                    yield break;
                }

                yield return null;
            }

            fail("VIEW_TIMEOUT " + label);
        }

        private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs e)
        {
            _lastReceipt = e;
        }

        private static bool IsRecorded(TableReceiptResolvedEventArgs? receipt)
        {
            return receipt != null &&
                   receipt.Command.LastReceipt == ReceiptStatus.Recorded &&
                   receipt.Resolution.Disposition == OutboxDisposition.Recorded;
        }

        private static TablePresenter RequirePresenter()
        {
            TablePresenter? presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            if (presenter == null)
            {
                throw new InvalidOperationException("TABLE_PRESENTER_MISSING");
            }

            return presenter;
        }

        private void Record(string line)
        {
            string entry = DateTime.UtcNow.ToString("O") + " " + line;
            _log.AppendLine(entry);
            try
            {
                File.AppendAllText(Path.Combine(_outputDirectory, "probe.log"), entry + Environment.NewLine);
            }
            catch
            {
                // Best effort; Debug.Log remains the process log.
            }

            Debug.Log("CONTROL_PROBE " + line);
        }
    }
}
