#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq.Expressions;
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
        private SessionCoordinator? _activeCoordinator;
        private SessionCoordinator? _roomAnchor;
        private readonly object _aiDecisionGate = new object();
        private readonly TaskCompletionSource<bool> _aiSubmitRelease =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private AiDecisionCapture? _capturedAiDecision;
        private object? _localDriver;
        private PropertyInfo? _chosenHook;
        private PropertyInfo? _beforeSubmitHook;
        private ulong _takeoverEpoch;

        private sealed class AiDecisionCapture
        {
            public AiDecisionCapture(int seat, string roundId, string decisionId, ulong controlEpoch,
                string actionKind, string? optionId, uint[] tileIds)
            {
                Seat = seat;
                RoundId = roundId;
                DecisionId = decisionId;
                ControlEpoch = controlEpoch;
                ActionKind = actionKind;
                OptionId = optionId;
                TileIds = tileIds;
            }

            public int Seat { get; }
            public string RoundId { get; }
            public string DecisionId { get; }
            public ulong ControlEpoch { get; }
            public string ActionKind { get; }
            public string? OptionId { get; }
            public uint[] TileIds { get; }
        }

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
            _aiSubmitRelease.TrySetResult(true);
            ClearAiProbeHooks();

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
            yield return WaitForView(root, presenter, view => view.Control != null && !view.Control.ControlledByComputer,
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
            _takeoverEpoch = initialEpoch + 1UL;
            InstallAiProbeHooks(root);
            Record("INITIAL epoch=" + initialEpoch + " decisionEpoch=" + oldEpoch);

            yield return ClickAndWait(root, presenter, "_releaseControlButton",
                CommandKind.ReleaseControl, "release", fail);
            if (!IsRecorded(_lastReceipt))
            {
                fail("RELEASE_NOT_RECORDED");
                complete(false);
                yield break;
            }

            yield return WaitForAiDecision(root, fail);
            AiDecisionCapture? capturedAi = GetCapturedAiDecision();
            if (capturedAi == null)
            {
                fail("TAKEOVER_AI_DECISION_NOT_CAPTURED");
                complete(false);
                yield break;
            }

            yield return Reconnect(root, fail);
            presenter = RequirePresenter();
            yield return WaitForView(root, presenter, view => view.Control != null &&
                                                          view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 1UL,
                "automated control after reconnect", fail);
            PlayerTableViewState automated = presenter.LatestView!;
            if (automated.Identity.RoundId != initialRound || automated.Identity.StateVersion < initial.Identity.StateVersion ||
                automated.Control?.ControlEpoch != initialEpoch + 1UL)
            {
                fail("REENTRY_RECONCILIATION_DIVERGED room=" + automated.Identity.RoomId +
                     " round=" + automated.Identity.RoundId + " state=" + automated.Identity.StateVersion +
                     " controlledByComputer=" + (automated.Control?.ControlledByComputer.ToString() ?? "<null>") +
                     " epoch=" + (automated.Control?.ControlEpoch.ToString() ?? "<null>") +
                     " decision=" + (automated.Own?.Decision?.DecisionId ?? "<null>") +
                     " decisionEpoch=" + (automated.Own?.Decision?.ControlEpoch.ToString() ?? "<null>"));
                complete(false);
                yield break;
            }
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
            yield return WaitForView(root, presenter, view => view.Control != null &&
                                                          !view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 2UL,
                "human control after reclaim", fail);
            PlayerTableViewState reclaimedControl = presenter.LatestView!;
            if (reclaimedControl.Identity.RoundId != initialRound ||
                reclaimedControl.Identity.StateVersion < automated.Identity.StateVersion)
            {
                fail("CLAIM_REENTRY_RECONCILIATION_DIVERGED");
                complete(false);
                yield break;
            }

            yield return WaitForView(root, presenter, view => view.Own?.Decision != null,
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
            Task<CommandSubmission> submit = _activeCoordinator!.SubmitAsync(
                stale,
                CancellationToken.None);
            while (!submit.IsCompleted) yield return null;
            yield return WaitForReceipt(root, CommandKind.SubmitAction, "stale action receipt", fail);
            if (_lastReceipt == null ||
                _lastReceipt.Command.LastReceipt != ReceiptStatus.Rejected ||
                _lastReceipt.Resolution.ReasonCode != ProtocolErrorCode.StaleControlEpoch)
            {
                fail("STALE_ACTION_NOT_REJECTED_AS_STALE_CONTROL_EPOCH reason=" +
                     (_lastReceipt?.Resolution.ReasonCode ?? "<none>"));
                complete(false);
                yield break;
            }

            ProtocolActionType oldAiActionType = ToProtocolAction(capturedAi.ActionKind);
            var staleAiResult = new ProtocolCommand(
                CommandKind.SubmitAction,
                CommandPayloadFactory.SubmitAction(
                    capturedAi.RoundId,
                    capturedAi.DecisionId,
                    capturedAi.ControlEpoch,
                    oldAiActionType,
                    string.IsNullOrEmpty(capturedAi.OptionId) ? null : capturedAi.OptionId,
                    capturedAi.TileIds,
                    meldType: capturedAi.ActionKind == "Kan" ? 4U : (uint?)null),
                new RequestId("probe-stale-ai-" + Guid.NewGuid().ToString("N")),
                new RequestScopeId(reclaimedControl.Identity.ScopeId),
                new StateVersion(reclaimedControl.Identity.StateVersion),
                new RoundId(capturedAi.RoundId),
                new DecisionId(capturedAi.DecisionId),
                new ControlEpoch(capturedAi.ControlEpoch),
                new OwnerEpoch(reclaimedControl.Identity.OwnerEpoch));
            _lastReceipt = null;
            Task<CommandSubmission> staleAiSubmit = _activeCoordinator!.SubmitAsync(
                staleAiResult,
                CancellationToken.None);
            while (!staleAiSubmit.IsCompleted) yield return null;
            if (staleAiSubmit.IsFaulted || staleAiSubmit.IsCanceled || !staleAiSubmit.Result.Sent)
            {
                fail("STALE_AI_RESULT_NOT_SENT");
                complete(false);
                yield break;
            }
            yield return WaitForReceipt(root, CommandKind.SubmitAction, "stale ai result receipt", fail);
            if (_lastReceipt == null ||
                _lastReceipt.Command.RequestId != staleAiResult.RequestId.Value ||
                _lastReceipt.Command.LastReceipt != ReceiptStatus.Rejected ||
                _lastReceipt.Resolution.ReasonCode != ProtocolErrorCode.StaleControlEpoch)
            {
                fail("STALE_AI_RESULT_NOT_REJECTED_AS_STALE_CONTROL_EPOCH reason=" +
                     (_lastReceipt?.Resolution.ReasonCode ?? "<none>"));
                complete(false);
                yield break;
            }
            _aiSubmitRelease.TrySetResult(true);

            Record("RESULT initialEpoch=" + initialEpoch +
                   " releaseEpoch=" + (initialEpoch + 1UL) +
                   " claimEpoch=" + (initialEpoch + 2UL) +
                   " oldEpoch=" + oldEpoch +
                   " stalePlayerReason=StaleControlEpoch" +
                   " oldAiDecision=" + capturedAi.DecisionId +
                   " staleAiReason=StaleControlEpoch" +
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

            _activeCoordinator = coordinator;
            coordinator.ReceiptResolved -= OnReceiptResolved;
            coordinator.ReceiptResolved += OnReceiptResolved;
            TablePresenter presenter = RequirePresenter();
            presenter.Bind(coordinator, root.Router, coordinator, null);
            yield return WaitForView(root, presenter, view => view.Identity.IsPrivateAuthorized,
                "baseline " + phase, message => throw new InvalidOperationException(message));
        }

        private void InstallAiProbeHooks(AppRoot root)
        {
            object factory = root.Composition.ChannelFactory;
            object? room = factory.GetType().GetField("_room", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(factory);
            object? authority = room?.GetType().GetField("_authority", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(room);
            _localDriver = authority?.GetType().GetProperty("DriverForTest", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(authority);
            if (_localDriver == null) throw new InvalidOperationException("LOCAL_AI_PROBE_DRIVER_UNAVAILABLE");

            _chosenHook = _localDriver.GetType().GetProperty("AiDecisionChosenForTest", BindingFlags.Instance | BindingFlags.NonPublic);
            _beforeSubmitHook = _localDriver.GetType().GetProperty("BeforeAiSubmitForTest", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_chosenHook == null || _beforeSubmitHook == null) throw new InvalidOperationException("LOCAL_AI_PROBE_HOOKS_UNAVAILABLE");
            _chosenHook.SetValue(_localDriver, CreateAiHook(_chosenHook.PropertyType, nameof(CaptureAiDecisionObject)));
            _beforeSubmitHook.SetValue(_localDriver, CreateAiHook(_beforeSubmitHook.PropertyType, nameof(HoldAiSubmitObject)));
            Record("AI_PROBE_HOOKS_INSTALLED");
        }

        private Delegate CreateAiHook(Type delegateType, string methodName)
        {
            MethodInfo invoke = delegateType.GetMethod("Invoke")!;
            ParameterInfo[] signature = invoke.GetParameters();
            var parameters = new ParameterExpression[signature.Length];
            var arguments = new Expression[signature.Length];
            for (int i = 0; i < signature.Length; i++)
            {
                parameters[i] = Expression.Parameter(signature[i].ParameterType, "arg" + i);
                arguments[i] = Expression.Convert(parameters[i], typeof(object));
            }
            MethodInfo method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodCallExpression body = Expression.Call(Expression.Constant(this), method, arguments);
            return Expression.Lambda(delegateType, body, parameters).Compile();
        }

        private void ClearAiProbeHooks()
        {
            try
            {
                _chosenHook?.SetValue(_localDriver, null);
                _beforeSubmitHook?.SetValue(_localDriver, null);
            }
            catch { }
        }

        private void CaptureAiDecisionObject(object seatValue, object observation, object action)
        {
            int seat = Convert.ToInt32(seatValue);
            ulong epoch = Convert.ToUInt64(ReadProperty(observation, "ControlEpoch"));
            if (seat != 0 || epoch != _takeoverEpoch) return;
            string roundId = Convert.ToString(ReadProperty(observation, "RoundId")) ?? string.Empty;
            string decisionId = Convert.ToString(ReadProperty(observation, "DecisionId")) ?? string.Empty;
            string actionKind = Convert.ToString(ReadProperty(action, "Kind")) ?? string.Empty;
            string? optionId = Convert.ToString(ReadProperty(action, "OptionId"));
            var tiles = new List<uint>();
            if (ReadProperty(action, "Tiles") is IEnumerable values)
            {
                foreach (object? tile in values)
                    if (tile != null) tiles.Add(Convert.ToUInt32(ReadProperty(tile, "Value")));
            }
            lock (_aiDecisionGate)
            {
                if (_capturedAiDecision == null)
                    _capturedAiDecision = new AiDecisionCapture(seat, roundId, decisionId, epoch, actionKind, optionId, tiles.ToArray());
            }
        }

        private Task HoldAiSubmitObject(object seatValue, object observation)
        {
            int seat = Convert.ToInt32(seatValue);
            ulong epoch = Convert.ToUInt64(ReadProperty(observation, "ControlEpoch"));
            lock (_aiDecisionGate)
            {
                return seat == 0 && epoch == _takeoverEpoch &&
                       _capturedAiDecision != null
                    ? _aiSubmitRelease.Task
                    : Task.CompletedTask;
            }
        }

        private AiDecisionCapture? GetCapturedAiDecision()
        {
            lock (_aiDecisionGate) return _capturedAiDecision;
        }

        private IEnumerator WaitForAiDecision(AppRoot root, Action<string> fail)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                root.Composition.UpdateRoomViewers();
                AiDecisionCapture? capture = GetCapturedAiDecision();
                if (capture != null)
                {
                    Record("AI_DECISION_CAPTURED seat=" + capture.Seat +
                           " round=" + capture.RoundId +
                           " decision=" + capture.DecisionId +
                           " epoch=" + capture.ControlEpoch +
                           " action=" + capture.ActionKind +
                           " option=" + (capture.OptionId ?? string.Empty) +
                           " tiles=" + string.Join(",", capture.TileIds));
                    yield break;
                }
                yield return null;
            }
            fail("TAKEOVER_AI_DECISION_TIMEOUT");
        }

        private static object? ReadProperty(object instance, string name) =>
            instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);

        private static ProtocolActionType ToProtocolAction(string kind)
        {
            switch (kind)
            {
                case "Pass": return ProtocolActionType.Pass;
                case "Discard": return ProtocolActionType.DiscardTile;
                case "Tsumo": return ProtocolActionType.DeclareTsumo;
                case "Ron": return ProtocolActionType.DeclareRon;
                case "Chi": return ProtocolActionType.DeclareChi;
                case "Pon": return ProtocolActionType.DeclarePon;
                case "Kan": return ProtocolActionType.DeclareKan;
                case "Riichi": return ProtocolActionType.DeclareRiichi;
                default: throw new InvalidOperationException("Unsupported captured AI action: " + kind);
            }
        }

        private IEnumerator Reconnect(AppRoot root, Action<string> fail)
        {
            SessionCoordinator? current = _activeCoordinator;
            TablePresenter oldPresenter = RequirePresenter();
            PlayerTableViewState? before = oldPresenter.LatestView;
            if (current == null || before == null)
            {
                fail("RECONNECT_BASELINE_MISSING");
                yield break;
            }

            if (_roomAnchor == null)
            {
                Task<SessionCoordinator> anchorOpen = root.Composition.OpenRoomViewerAsync(
                    LocalRoomViewerRole.Spectator, -1, CancellationToken.None);
                while (!anchorOpen.IsCompleted) yield return null;
                if (anchorOpen.IsFaulted || anchorOpen.IsCanceled)
                {
                    fail("ROOM_REENTRY_ANCHOR_OPEN_FAILED");
                    yield break;
                }
                _roomAnchor = anchorOpen.Result;
            }

            current.ReceiptResolved -= OnReceiptResolved;
            Task<ShutdownResult> close = ReferenceEquals(current, root.Composition.SessionCoordinator)
                ? current.ShutdownAsync("CONTROL_PROBE_DISCONNECT")
                : root.Composition.CloseRoomViewerAsync(current, "CONTROL_PROBE_DISCONNECT");
            while (!close.IsCompleted) yield return null;
            if (close.IsFaulted || close.IsCanceled || close.Result.Outcome != ShutdownOutcome.Completed || close.Result.Quarantined)
            {
                fail("ROOM_REENTRY_DISCONNECT_FAILED");
                yield break;
            }

            Task<SessionCoordinator> reopen = root.Composition.OpenRoomViewerAsync(
                LocalRoomViewerRole.Player, 0, CancellationToken.None);
            while (!reopen.IsCompleted) yield return null;
            if (reopen.IsFaulted || reopen.IsCanceled)
            {
                fail("ROOM_REENTRY_OPEN_FAILED");
                yield break;
            }

            SessionCoordinator next = reopen.Result;
            next.ReceiptResolved -= OnReceiptResolved;
            next.ReceiptResolved += OnReceiptResolved;
            TablePresenter presenter = RequirePresenter();
            presenter.Bind(next, root.Router, next, null);
            _activeCoordinator = next;
            yield return WaitForView(root, presenter, view => view.Identity.RoomId == before.Identity.RoomId &&
                view.Identity.RoundId == before.Identity.RoundId &&
                view.Identity.StateVersion >= before.Identity.StateVersion,
                "same-room re-entry reconciliation", fail);
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
            yield return WaitForReceipt(root, kind, phase + " receipt", fail);
        }

        private IEnumerator WaitForReceipt(AppRoot root, CommandKind kind, string label, Action<string> fail)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                root.Composition.UpdateRoomViewers();
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
            AppRoot root,
            TablePresenter presenter,
            Func<PlayerTableViewState, bool> predicate,
            string label,
            Action<string> fail)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                root.Composition.UpdateRoomViewers();
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
