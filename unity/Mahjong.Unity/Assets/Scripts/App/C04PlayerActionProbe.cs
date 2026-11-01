#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Opt-in, one-case Windows Player regression. It enters through the normal
    /// Menu/Lobby/Table composition, builds a candidate-bound intent with the
    /// production TableCommandIntentFactory, submits through the production
    /// ITableCommandSink/SessionCoordinator, and requires the accepted visible
    /// event observer to pair the Applied receipt with the exact round/seat.
    /// </summary>
    public sealed class C04PlayerActionProbe : MonoBehaviour
    {
        public const string ArgumentName = "-c04-player-actions";
        public const string CaseArgumentName = "-c04-player-action-case";
        public const string RunIdArgumentName = "-c04-player-action-run-id";

        private const float TimeoutSeconds = 150f;
        private string _root = string.Empty;
        private string _caseId = string.Empty;
        private string _runId = string.Empty;
        private string _outcome = "NOT_RUN";
        private string _failure = string.Empty;
        private bool _finished;
        private int _exitCode = 67;
        private string _evidence = string.Empty;
        private C04PlayerActionSummary? _summary;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            string? evidenceRoot = Arg(args, ArgumentName);
            if (string.IsNullOrWhiteSpace(evidenceRoot)) return;
            string? caseId = Arg(args, CaseArgumentName);
            string? runId = Arg(args, RunIdArgumentName);
            string scenario = ScenarioForCase(caseId ?? string.Empty);
            LocalTableChannelFactory.EnablePlayerActionScenario(scenario);
            Application.runInBackground = true;
            var go = new GameObject("C04PlayerActionProbe");
            DontDestroyOnLoad(go);
            var probe = go.AddComponent<C04PlayerActionProbe>();
            probe._root = Path.GetFullPath(evidenceRoot!);
            probe._caseId = caseId!;
            probe._runId = runId ?? string.Empty;
        }

        private void Start()
        {
            if (string.IsNullOrWhiteSpace(_root)) return;
            try
            {
                Directory.CreateDirectory(_root);
                _evidence = Path.Combine(_root, "player-action.jsonl");
                if (File.Exists(_evidence) || File.Exists(Path.Combine(_root, "summary.json")))
                    throw new IOException("C04 evidence destination already contains a sealed result.");
                StartCoroutine(Run());
            }
            catch (Exception exception)
            {
                Fail("EVIDENCE_CREATE_FAILED", exception.GetType().Name + ": " + exception.Message);
                Finish();
            }
        }

        private IEnumerator Run()
        {
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (AppRoot.Instance == null && Time.realtimeSinceStartup < deadline) yield return null;
            if (AppRoot.Instance?.Router == null) { Fail("APP_ROOT_MISSING", "The production AppRoot was not initialized."); yield break; }
            AppRouter router = AppRoot.Instance.Router;
            while (router.CurrentScene != SceneId.Menu && Time.realtimeSinceStartup < deadline) yield return null;
            if (router.CurrentScene != SceneId.Menu) { Fail("MENU_NOT_READY", "The Menu scene did not load."); yield break; }
            if (!Click("PlayButton")) { Fail("PLAY_BUTTON_MISSING", "The production PlayButton was not present."); yield break; }
            deadline = Time.realtimeSinceStartup + 45f;
            while (router.CurrentScene != SceneId.Lobby && Time.realtimeSinceStartup < deadline) yield return null;
            if (router.CurrentScene != SceneId.Lobby) { Fail("LOBBY_NOT_READY", "The Lobby scene did not load."); yield break; }
            if (!Click("StartLocalGameButton")) { Fail("LOCAL_START_MISSING", "The production local-start button was not present."); yield break; }
            deadline = Time.realtimeSinceStartup + 60f;
            while (router.CurrentScene != SceneId.Table && Time.realtimeSinceStartup < deadline) yield return null;
            if (router.CurrentScene != SceneId.Table) { Fail("TABLE_NOT_READY", "The Table scene did not load."); yield break; }

            AppComposition composition = AppRoot.Instance.Composition;
            float readyDeadline = Time.realtimeSinceStartup + 45f;
            SessionCoordinator? coordinator = composition.SessionCoordinator;
            while (Time.realtimeSinceStartup < readyDeadline &&
                   (coordinator == null || coordinator.Current.State != TableConnectionState.Ready || coordinator.Current.PlayerSnapshot == null))
            {
                yield return null;
                coordinator = composition.SessionCoordinator;
            }
            if (coordinator == null || coordinator.Current.State != TableConnectionState.Ready || coordinator.Current.PlayerSnapshot == null)
            {
                Fail("SESSION_NOT_READY", "The real TableSession did not publish a ready player snapshot.");
                yield break;
            }

            var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
            var intents = new TableCommandIntentFactory(new SystemTableClientClock());
            PlayerTableViewState view = mapper.Project(coordinator.Current);
            float candidateDeadline = Time.realtimeSinceStartup + 30f;
            PlayerLegalActionState? candidate = FindCandidate(view, _caseId);
            while (candidate == null && Time.realtimeSinceStartup < candidateDeadline)
            {
                yield return null;
                coordinator = composition.SessionCoordinator;
                if (coordinator == null) break;
                view = mapper.Project(coordinator.Current);
                candidate = FindCandidate(view, _caseId);
            }
            if (candidate == null || coordinator == null)
            {
                Fail("FIXED_CANDIDATE_MISSING", "The authoritative fixed scenario did not provide the required seat-0 candidate.");
                yield break;
            }

            string requestId = "c04-" + _runId + "-" + _caseId + "-seat0-1";
            var owner = new TableCommandOwnerContext(
                new RequestId(requestId),
                new RequestScopeId(view.Identity.ScopeId),
                new StateVersion(view.Identity.StateVersion),
                new OwnerEpoch(view.Identity.OwnerEpoch));
            TableCommandIntent intent;
            try { intent = intents.CreateSubmitAction(view, candidate, owner); }
            catch (Exception exception) { Fail("INTENT_REJECTED", exception.GetType().Name + ": " + exception.Message); yield break; }

            using var observer = new ConfirmedVisibleEventObserver(coordinator);
            EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => observer.RecordReceipt(args);
            coordinator.ReceiptResolved += receiptHandler;
            observer.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, candidate.Action);
            string payloadHash = Sha256(intent.Command.Payload.CopyBytes());
            string payloadBase64 = Convert.ToBase64String(intent.Command.Payload.CopyBytes());
            string baselineRound = view.Identity.RoundId;
            ulong baselineVersion = view.Identity.StateVersion;
            ulong baselineEventSeq = coordinator.Current.PlayerSnapshot!.EventSeq;
            ulong baselineCursor = coordinator.Current.Cursor?.Value ?? 0UL;
            Task<CommandSubmission> submit;
            try { submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None); }
            catch (Exception exception)
            {
                coordinator.ReceiptResolved -= receiptHandler;
                Fail("SUBMIT_THROW", exception.GetType().Name + ": " + exception.Message);
                yield break;
            }

            float actionDeadline = Time.realtimeSinceStartup + 45f;
            while (!submit.IsCompleted && Time.realtimeSinceStartup < actionDeadline) yield return null;
            if (!submit.IsCompleted || submit.IsFaulted || submit.IsCanceled || !submit.Result.Sent)
            {
                coordinator.ReceiptResolved -= receiptHandler;
                Fail("SUBMIT_NOT_SENT", submit.IsCompleted ? "The production sink did not hand the request to the channel." : "The original submit timed out.");
                yield break;
            }

            ConfirmedVisibleActionEvent? confirmation = null;
            while (Time.realtimeSinceStartup < actionDeadline)
            {
                confirmation = observer.TryGetConfirmation();
                if (observer.HasAppliedReceipt && confirmation != null) break;
                yield return null;
            }
            coordinator.ReceiptResolved -= receiptHandler;
            if (observer.HasAppliedReceipt && confirmation == null)
            {
                Fail("APPLIED_EVENT_MISSING", "Applied receipt arrived without its matching action event in an accepted incremental batch.");
                yield break;
            }
            if (confirmation == null)
            {
                Fail("ACTION_UNCONFIRMED", observer.ReceiptSummary + " " + observer.DescribeBatches());
                yield break;
            }

            SessionViewState finalState = coordinator.Current;
            _outcome = "PASS";
            _exitCode = 0;
            _summary = new C04PlayerActionSummary
            {
                Result = _outcome,
                CaseId = _caseId,
                RunId = _runId,
                RequestId = requestId,
                ScopeId = view.Identity.ScopeId,
                PayloadSha256 = payloadHash,
                CanonicalPayloadBase64 = payloadBase64,
                RoomId = view.Identity.RoomId,
                SessionId = view.Identity.SessionId,
                Seat = view.Identity.Seat,
                RoundId = baselineRound,
                DecisionId = candidate.DecisionId,
                ControlEpoch = candidate.ControlEpoch,
                BindingEpoch = view.Identity.BindingEpoch,
                OwnerEpoch = view.Identity.OwnerEpoch,
                StateVersionBefore = baselineVersion,
                StateVersionAfter = finalState.PlayerSnapshot?.StateVersion ?? 0UL,
                EventSeqBefore = baselineEventSeq,
                EventSeqAfter = finalState.PlayerSnapshot?.EventSeq ?? 0UL,
                CursorBefore = baselineCursor,
                CursorAfter = finalState.Cursor?.Value ?? 0UL,
                Receipt = observer.ReceiptSummary,
                ReceiptStatus = observer.HasAppliedReceipt ? "Applied" : "Missing",
                ReceiptRequestId = requestId,
                ReceiptScopeId = view.Identity.ScopeId,
                ReceiptCommandVersion = observer.AppliedCommandVersion ?? 0UL,
                EventType = confirmation.Event.Type.ToString(),
                EventSeat = confirmation.Event.Seat,
                EventRoundId = confirmation.Batch.RoundId,
                EventCommandVersion = confirmation.Event.CommandVersion,
                EventSeq = confirmation.Event.EventSeq,
                EventConfirmed = true,
                EventTiles = String.Join(",", confirmation.Event.TileIds),
                Batch = confirmation.Batch.Describe(),
                Status = "GAME"
            };
            yield return Finish();
        }

        private bool Click(string objectName)
        {
            GameObject? buttonObject = GameObject.Find(objectName);
            Button? button = buttonObject != null ? buttonObject.GetComponent<Button>() : null;
            if (button == null || !button.interactable) return false;
            button.onClick.Invoke();
            return true;
        }

        private static PlayerLegalActionState? FindCandidate(PlayerTableViewState view, string caseId)
        {
            if (view.Own?.Decision == null || view.Identity.Seat != 0) return null;
            PlayerActionKind kind = ActionForCase(caseId);
            for (int i = 0; i < view.Own.Decision.Candidates.Count; i++)
            {
                PlayerLegalActionState candidate = view.Own.Decision.Candidates[i];
                if (candidate.Action != kind) continue;
                if (kind == PlayerActionKind.DeclareKan && !candidate.OptionId.StartsWith("closed-kan-", StringComparison.Ordinal)) continue;
                return candidate;
            }
            return null;
        }

        private void Fail(string code, string detail)
        {
            _outcome = "FAIL";
            _failure = code + ": " + detail;
            _exitCode = 67;
            Debug.LogError("C04_PLAYER_ACTION_FAIL " + _failure);
            Finish();
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (!string.IsNullOrEmpty(_root))
            {
                if (_summary == null) _summary = new C04PlayerActionSummary
                {
                    Result = _outcome,
                    CaseId = _caseId,
                    RunId = _runId,
                    Failure = _failure
                };
                try { WriteSummary(_summary); }
                catch (Exception exception)
                {
                    Debug.LogError("C04_PLAYER_ACTION_SUMMARY_FAILED " + exception.GetType().Name + ": " + exception.Message);
                    _exitCode = 68;
                }
            }
            Task shutdown;
            try { shutdown = AppRoot.Instance?.Composition.ShutdownAsync("C04_PLAYER_ACTION_END") ?? Task.CompletedTask; }
            catch { shutdown = Task.CompletedTask; }
            StartCoroutine(WaitShutdown(shutdown));
        }

        private IEnumerator WaitShutdown(Task shutdown)
        {
            float deadline = Time.realtimeSinceStartup + 8f;
            while (!shutdown.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Application.Quit(_exitCode);
        }

        private void WriteSummary(C04PlayerActionSummary value)
        {
            if (string.IsNullOrEmpty(_root)) return;
            string path = Path.Combine(_root, "summary.json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(value, true) + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static string ScenarioForCase(string caseId)
        {
            switch (caseId)
            {
                case "C04-G2-01": return LocalPlayerActionScenario.Pon;
                case "C04-G2-02": return LocalPlayerActionScenario.Chi;
                case "C04-G2-03": return LocalPlayerActionScenario.Riichi;
                case "C04-G2-04": return LocalPlayerActionScenario.ClosedKan;
                default: throw new ArgumentOutOfRangeException(nameof(caseId), "Unknown frozen C04-G2 case id.");
            }
        }

        private static PlayerActionKind ActionForCase(string caseId)
        {
            switch (caseId)
            {
                case "C04-G2-01": return PlayerActionKind.DeclarePon;
                case "C04-G2-02": return PlayerActionKind.DeclareChi;
                case "C04-G2-03": return PlayerActionKind.DeclareRiichi;
                case "C04-G2-04": return PlayerActionKind.DeclareKan;
                default: throw new ArgumentOutOfRangeException(nameof(caseId));
            }
        }

        private static string? Arg(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static string Sha256(byte[] value)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", string.Empty).ToLowerInvariant();
        }

        [Serializable]
        private sealed class C04PlayerActionSummary
        {
            public string Result = "NOT_RUN";
            public string CaseId = string.Empty;
            public string RunId = string.Empty;
            public string RequestId = string.Empty;
            public string ScopeId = string.Empty;
            public string PayloadSha256 = string.Empty;
            public string CanonicalPayloadBase64 = string.Empty;
            public string RoomId = string.Empty;
            public string SessionId = string.Empty;
            public int Seat = -1;
            public string RoundId = string.Empty;
            public string DecisionId = string.Empty;
            public ulong ControlEpoch;
            public ulong BindingEpoch;
            public ulong OwnerEpoch;
            public ulong StateVersionBefore;
            public ulong StateVersionAfter;
            public ulong EventSeqBefore;
            public ulong EventSeqAfter;
            public ulong CursorBefore;
            public ulong CursorAfter;
            public string Receipt = string.Empty;
            public string ReceiptStatus = string.Empty;
            public string ReceiptRequestId = string.Empty;
            public string ReceiptScopeId = string.Empty;
            public ulong ReceiptCommandVersion;
            public string EventType = string.Empty;
            public int EventSeat = -1;
            public string EventRoundId = string.Empty;
            public ulong EventCommandVersion;
            public ulong EventSeq;
            public bool EventConfirmed;
            public string EventTiles = string.Empty;
            public string Batch = string.Empty;
            public string Status = string.Empty;
            public string Failure = string.Empty;
        }
    }
}
