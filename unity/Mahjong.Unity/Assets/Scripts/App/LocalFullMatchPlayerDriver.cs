#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WirePlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Opt-in full-match Player driver. It is created only when the process was
    /// launched with <c>-full-match-driver &lt;absolute evidence dir&gt;</c>; a
    /// normal application launch never constructs it and never enables match mode.
    ///
    /// <para>
    /// It walks the real production Menu -&gt; Lobby -&gt; local table route, then
    /// submits only server-provided candidates through the existing Client session
    /// / ITableCommandSink. Round boundaries go through the accepted
    /// <see cref="LocalMatchCoordinator"/> facade exposed by
    /// <see cref="LocalTableChannelFactory"/>; it never calls Domain, TableActor or
    /// LocalHost internals and never writes authoritative state.
    /// </para>
    ///
    /// <para>
    /// The first failure is retained in the evidence directory and the process
    /// exits non-zero. It never retries an unknown request under a new id and never
    /// treats a timeout, a missing event or a bounded action cap as success.
    /// </para>
    /// </summary>
    public sealed class LocalFullMatchPlayerDriver : MonoBehaviour
    {
        public const string DriverVersion = "p45-full-match-driver/1";
        public const string ArgumentName = "-full-match-driver";

        private const int ExitNavigation = 40;
        private const int ExitSession = 41;
        private const int ExitNoDecision = 42;
        private const int ExitActionUnconfirmed = 43;
        private const int ExitMissingEvent = 44;
        private const int ExitRoundTransition = 45;
        private const int ExitMatchNotFinished = 46;
        private const int ExitFinalConsistency = 47;
        private const int ExitUnexpected = 48;
        private const int ExitTimeout = 49;

        private const float NavigationTimeoutSeconds = 30f;
        private const float SessionReadyTimeoutSeconds = 45f;
        private const float SubmitTimeoutSeconds = 20f;
        private const float ConfirmationTimeoutSeconds = 30f;
        private const float RoundTimeoutSeconds = 1200f;
        private const float MatchTimeoutSeconds = 5400f;
        private const float StallDetectionSeconds = 25f;
        private const int MaxSessionRecoveriesPerRound = 3;

        private readonly FullMatchRoundGuard _roundGuard = new FullMatchRoundGuard();
        private readonly FullMatchRequestLedger _requestLedger = new FullMatchRequestLedger();
        private readonly List<FullMatchRoundTerminal> _terminals = new List<FullMatchRoundTerminal>();
        private readonly List<DriverActionRecord> _actions = new List<DriverActionRecord>();
        private readonly List<string> _observedScenarios = new List<string>();

        private string _evidenceDir = string.Empty;
        private StreamWriter? _log;
        private bool _completed;
        private bool _failed;
        private bool _finishStarted;
        private string _failureCode = string.Empty;
        private string _failureDetail = string.Empty;
        private int _failureExit = ExitUnexpected;
        private string _finishedAt = string.Empty;
        private string _lastCompletedDecisionKey = string.Empty;
        private string _lastCompletedDrawKey = string.Empty;
        private string _lastObservedRoundId = string.Empty;
        private ulong _lastObservedStateVersion;
        private float _lastProgressAt;
        private int _sessionRecoveries;
        private LocalMatchSnapshot? _lastMatch;

        /// <summary>
        /// Registers the opt-in driver before any scene or <c>AppRoot</c> exists.
        /// The match-mode switch must be set before AppComposition captures its
        /// channel factory, otherwise the driver would open a normal single round.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            string? evidenceDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ArgumentName, StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    evidenceDir = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(evidenceDir))
            {
                return;
            }

            LocalTableChannelFactory.EnableMatchMode();
            Application.runInBackground = true;
            var go = new GameObject("LocalFullMatchPlayerDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            LocalFullMatchPlayerDriver driver = go.AddComponent<LocalFullMatchPlayerDriver>();
            driver._evidenceDir = evidenceDir!;
        }

        private void Start()
        {
            if (string.IsNullOrWhiteSpace(_evidenceDir))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_evidenceDir);
                _log = new StreamWriter(
                    Path.Combine(_evidenceDir, "full-match.jsonl"),
                    append: false,
                    new UTF8Encoding(false));
                _log.AutoFlush = true;
            }
            catch (Exception exception)
            {
                Debug.LogError("FULL_MATCH_EVIDENCE_OPEN_FAILED: " + exception.GetType().Name);
                Application.Quit(ExitUnexpected);
                return;
            }

            LogEvent("driver_start", "\"version\":\"" + DriverVersion + "\"");
            StartCoroutine(RunMatch());
        }

        /// <summary>
        /// C# iterator methods may not yield from a try block with a catch. This
        /// wrapper owns the exception boundary; the production step machine below
        /// only uses try/finally for observer cleanup.
        /// </summary>
        private IEnumerator RunMatch()
        {
            IEnumerator core = RunMatchCore();
            while (true)
            {
                bool moved;
                try
                {
                    moved = core.MoveNext();
                }
                catch (Exception exception)
                {
                    Fail(ExitUnexpected, "DRIVER_EXCEPTION", exception.GetType().Name + ": " + exception.Message);
                    moved = false;
                }

                if (!moved)
                {
                    break;
                }

                yield return core.Current;
            }

            if (!_completed && !_failed)
            {
                Fail(ExitUnexpected, "DRIVER_INCOMPLETE", "The full-match driver ended without a terminal result.");
            }

            yield return Finish();
        }

        private IEnumerator RunMatchCore()
        {
            float matchTimeout = Time.realtimeSinceStartup + MatchTimeoutSeconds;
            AppComposition? composition = null;
            for (int frames = 0; frames < 60 && AppRoot.Instance == null; frames++)
            {
                yield return null;
            }

                if (AppRoot.Instance == null || AppRoot.Instance.Router == null)
                {
                    Fail(ExitNavigation, "APP_ROOT_MISSING", "AppRoot/Router did not initialize.");
                    yield return Finish();
                    yield break;
                }

                AppRouter router = AppRoot.Instance.Router;
                for (int frames = 0; frames < 600 && router.CurrentScene != SceneId.Menu; frames++)
                {
                    yield return null;
                }

                if (router.CurrentScene != SceneId.Menu)
                {
                    Fail(ExitNavigation, "MENU_NOT_READY", "The real Menu scene did not load.");
                    yield return Finish();
                    yield break;
                }

                LogEvent("scene", "\"scene\":\"Menu\"");
                if (!Click("PlayButton"))
                {
                    Fail(ExitNavigation, "PLAY_BUTTON_MISSING", "The real PlayButton was not present.");
                    yield return Finish();
                    yield break;
                }

                yield return WaitForScene(router, SceneId.Lobby, NavigationTimeoutSeconds);
                if (router.CurrentScene != SceneId.Lobby)
                {
                    Fail(ExitNavigation, "LOBBY_NOT_READY", "The real Lobby scene did not load.");
                    yield return Finish();
                    yield break;
                }

                LogEvent("scene", "\"scene\":\"Lobby\"");
                GameObject? start = GameObject.Find("StartLocalGameButton");
                Button? startButton = start != null ? start.GetComponent<Button>() : null;
                if (startButton == null || !startButton.interactable)
                {
                    Fail(ExitNavigation, "START_LOCAL_UNAVAILABLE", "The real local start button is missing or disabled.");
                    yield return Finish();
                    yield break;
                }

                if (!Click("StartLocalGameButton"))
                {
                    Fail(ExitNavigation, "START_CLICK_FAILED", "The real local start button could not be clicked.");
                    yield return Finish();
                    yield break;
                }

                yield return WaitForScene(router, SceneId.Table, NavigationTimeoutSeconds);
                if (router.CurrentScene != SceneId.Table)
                {
                    Fail(ExitNavigation, "TABLE_NOT_READY", "The real Table scene did not load.");
                    yield return Finish();
                    yield break;
                }

                LogEvent("scene", "\"scene\":\"Table\"");
                composition = AppRoot.Instance.Composition;
                if (composition == null || !(composition.ChannelFactory is ILocalMatchControl matchControl))
                {
                    Fail(ExitSession, "MATCH_CONTROL_MISSING", "The production local channel factory exposes no match control.");
                    yield return Finish();
                    yield break;
                }

                if (!LocalTableChannelFactory.MatchModeEnabled)
                {
                    Fail(ExitSession, "MATCH_MODE_DISABLED", "Match composition was not enabled before AppRoot initialization.");
                    yield return Finish();
                    yield break;
                }

                _lastMatch = matchControl.CurrentMatch;
                if (_lastMatch == null)
                {
                    Fail(ExitSession, "MATCH_NOT_OPENED", "No durable local match was opened by the real session.");
                    yield return Finish();
                    yield break;
                }

                LogEvent(
                    "match_opened",
                    "\"matchId\":\"" + Json(_lastMatch.MatchId) + "\"" +
                    ",\"ordinal\":" + Num(_lastMatch.Ordinal) +
                    ",\"roundId\":\"" + Json(_lastMatch.RoundId) + "\"");

                bool finished = false;
                while (!finished)
                {
                    if (Time.realtimeSinceStartup > matchTimeout)
                    {
                        Fail(ExitTimeout, "MATCH_TIMEOUT", "The full-match driver exceeded its bounded match time.");
                        yield return Finish();
                        yield break;
                    }

                    SessionCoordinator? coordinator = composition.SessionCoordinator;
                    if (coordinator == null)
                    {
                        Fail(ExitSession, "SESSION_MISSING", "No real Client session is open.");
                        yield return Finish();
                        yield break;
                    }

                    float readyDeadline = Time.realtimeSinceStartup + SessionReadyTimeoutSeconds;
                    while (Time.realtimeSinceStartup < readyDeadline &&
                           (coordinator.Current.State != TableConnectionState.Ready ||
                            coordinator.Current.Binding == null ||
                            coordinator.Current.PlayerSnapshot == null))
                    {
                        yield return null;
                        coordinator = composition.SessionCoordinator;
                        if (coordinator == null)
                        {
                            break;
                        }
                    }

                    if (coordinator == null ||
                        coordinator.Current.State != TableConnectionState.Ready ||
                        coordinator.Current.Binding == null ||
                        coordinator.Current.PlayerSnapshot == null)
                    {
                        Fail(ExitSession, "SESSION_NOT_READY", "The real Client session did not reach Ready with a snapshot.");
                        yield return Finish();
                        yield break;
                    }

                    // One clock instance anchors both the server-relative countdown
                    // observation (mapper) and the deadline comparison (intent
                    // factory). Two independent Stopwatches would corrupt every
                    // countdown by their start-time difference.
                    var roundClock = new SystemTableClientClock();
                    var mapper = new PlayerTableProjectionMapper(roundClock);
                    var intentFactory = new TableCommandIntentFactory(roundClock);
                    PlayerTableViewState view = mapper.Project(coordinator.Current);
                    if (!string.Equals(view.Identity.RoundId, _lastMatch.RoundId, StringComparison.Ordinal))
                    {
                        Fail(
                            ExitRoundTransition,
                            "ROUND_ID_MISMATCH",
                            "The client round '" + view.Identity.RoundId + "' does not equal the durable match round '" +
                            _lastMatch.RoundId + "'.");
                        yield return Finish();
                        yield break;
                    }

                    float roundDeadline = Time.realtimeSinceStartup + RoundTimeoutSeconds;
                    bool roundFinished = false;
                    while (!roundFinished)
                    {
                        if (Time.realtimeSinceStartup > matchTimeout)
                        {
                            Fail(ExitTimeout, "MATCH_TIMEOUT", "The full-match driver exceeded its bounded match time.");
                            yield return Finish();
                            yield break;
                        }

                        if (Time.realtimeSinceStartup > roundDeadline)
                        {
                            Fail(ExitTimeout, "ROUND_TIMEOUT", "Round '" + view.Identity.RoundId + "' exceeded its bounded time.");
                            yield return Finish();
                            yield break;
                        }

                        coordinator = composition.SessionCoordinator;
                        if (coordinator == null)
                        {
                            Fail(ExitSession, "SESSION_LOST", "The real Client session disappeared mid-round.");
                            yield return Finish();
                            yield break;
                        }

                        view = mapper.Project(coordinator.Current);
                        if (!string.Equals(view.Identity.RoundId, _lastObservedRoundId, StringComparison.Ordinal) ||
                            view.Identity.StateVersion != _lastObservedStateVersion)
                        {
                            _lastObservedRoundId = view.Identity.RoundId;
                            _lastObservedStateVersion = view.Identity.StateVersion;
                            _lastProgressAt = Time.realtimeSinceStartup;
                        }
                        else if (view.ConnectionState == TableConnectionState.Ready &&
                                 Time.realtimeSinceStartup - _lastProgressAt > StallDetectionSeconds)
                        {
                            if (_sessionRecoveries >= MaxSessionRecoveriesPerRound)
                            {
                                Fail(
                                    ExitTimeout,
                                    "ROUND_STALLED",
                                    "Round '" + view.Identity.RoundId + "' made no confirmed progress for " +
                                    StallDetectionSeconds + "s after " + _sessionRecoveries + " recoveries.");
                                yield return Finish();
                                yield break;
                            }

                            _sessionRecoveries++;
                            LogEvent(
                                "session_recovery_start",
                                "\"roundId\":\"" + Json(view.Identity.RoundId) + "\"" +
                                ",\"stateVersion\":" + Num(view.Identity.StateVersion) +
                                ",\"attempt\":" + Num(_sessionRecoveries));

                            Task<ShutdownResult> recoverShutdown = composition.ShutdownAsync("MATCH_STALL_RECOVERY");
                            float recoverShutdownDeadline = Time.realtimeSinceStartup + 20f;
                            while (!recoverShutdown.IsCompleted && Time.realtimeSinceStartup < recoverShutdownDeadline)
                            {
                                yield return null;
                            }

                            if (!recoverShutdown.IsCompleted || recoverShutdown.IsFaulted || recoverShutdown.IsCanceled)
                            {
                                Fail(ExitSession, "RECOVERY_SHUTDOWN_FAILED", describeTaskFailure(recoverShutdown));
                                yield return Finish();
                                yield break;
                            }

                            ShutdownResult recoverResult = recoverShutdown.Result;
                            if (recoverResult.Outcome != ShutdownOutcome.Completed || recoverResult.Quarantined)
                            {
                                Fail(
                                    ExitSession,
                                    "RECOVERY_SHUTDOWN_INCOMPLETE",
                                    "Recovery shutdown outcome=" + recoverResult.Outcome +
                                    " quarantined=" + recoverResult.Quarantined + ".");
                                yield return Finish();
                                yield break;
                            }

                            Task<SessionCoordinator> recoverOpen = composition.OpenLocalSessionAsync(CancellationToken.None);
                            float recoverOpenDeadline = Time.realtimeSinceStartup + 30f;
                            while (!recoverOpen.IsCompleted && Time.realtimeSinceStartup < recoverOpenDeadline)
                            {
                                yield return null;
                            }

                            if (!recoverOpen.IsCompleted || recoverOpen.IsFaulted || recoverOpen.IsCanceled)
                            {
                                Fail(ExitSession, "RECOVERY_OPEN_FAILED", describeTaskFailure(recoverOpen));
                                yield return Finish();
                                yield break;
                            }

                            coordinator = recoverOpen.Result;
                            float recoverReady = Time.realtimeSinceStartup + SessionReadyTimeoutSeconds;
                            while (Time.realtimeSinceStartup < recoverReady &&
                                   (coordinator.Current.State != TableConnectionState.Ready ||
                                    coordinator.Current.PlayerSnapshot == null))
                            {
                                yield return null;
                                coordinator = composition.SessionCoordinator ?? coordinator;
                            }

                            if (coordinator.Current.State != TableConnectionState.Ready ||
                                coordinator.Current.PlayerSnapshot == null)
                            {
                                Fail(ExitSession, "RECOVERY_NOT_READY", "The recovered round session did not become Ready.");
                                yield return Finish();
                                yield break;
                            }

                            roundClock = new SystemTableClientClock();
                            mapper = new PlayerTableProjectionMapper(roundClock);
                            intentFactory = new TableCommandIntentFactory(roundClock);
                            _lastObservedRoundId = string.Empty;
                            _lastObservedStateVersion = 0UL;
                            _lastProgressAt = Time.realtimeSinceStartup;
                            LogEvent(
                                "session_recovery_ready",
                                "\"roundId\":\"" + Json(coordinator.Current.PlayerSnapshot.RoundId ?? string.Empty) + "\"" +
                                ",\"stateVersion\":" + Num(coordinator.Current.PlayerSnapshot.StateVersion));
                            yield return null;
                            continue;
                        }

                        FullMatchRoundTransition transition = _roundGuard.Observe(
                            view,
                            out string transitionCode,
                            out string transitionDetail);
                        if (transition == FullMatchRoundTransition.Failed)
                        {
                            Fail(ExitRoundTransition, transitionCode, transitionDetail);
                            yield return Finish();
                            yield break;
                        }

                        if (transition == FullMatchRoundTransition.Terminal)
                        {
                            LogEvent(
                                "round_end_observed",
                                "\"roundId\":\"" + Json(view.Identity.RoundId) + "\"" +
                                ",\"stateVersion\":" + Num(view.Identity.StateVersion) +
                                ",\"eventSeq\":" + Num(view.Identity.EventSeq));
                            WirePlayerSnapshot? raw = coordinator.Current.PlayerSnapshot;
                            string terminalCode = string.Empty;
                            string terminalDetail = string.Empty;
                            FullMatchRoundTerminal? terminal = null;
                            if (raw == null ||
                                !LocalFullMatchPolicy.TryCaptureTerminal(
                                    raw,
                                    view,
                                    out terminal,
                                    out terminalCode,
                                    out terminalDetail))
                            {
                                Fail(
                                    ExitFinalConsistency,
                                    string.IsNullOrEmpty(terminalCode) ? "TERMINAL_CAPTURE_FAILED" : terminalCode,
                                    string.IsNullOrEmpty(terminalDetail)
                                        ? "The confirmed terminal snapshot could not be captured."
                                        : terminalDetail);
                                yield return Finish();
                                yield break;
                            }

                            _terminals.Add(terminal!);
                            RecordScenarios(terminal!);
                            LogEvent(
                                "round_terminal",
                                "\"roundId\":\"" + Json(terminal!.RoundId) + "\"" +
                                ",\"outcome\":\"" + Json(terminal.Outcome) + "\"" +
                                ",\"winnerSeat\":" + Num(terminal.WinnerSeat) +
                                ",\"loserSeat\":" + Num(terminal.LoserSeat) +
                                ",\"scores\":[" + Scores(terminal.FinalScores) + "]" +
                                ",\"riichiSticks\":" + Num(terminal.RemainingRiichiSticks));

                            Task<LocalMatchSnapshot> recordTask = matchControl.RecordRoundCompletionAsync(CancellationToken.None);
                            float recordDeadline = Time.realtimeSinceStartup + 20f;
                            while (!recordTask.IsCompleted && Time.realtimeSinceStartup < recordDeadline)
                            {
                                yield return null;
                            }

                            if (!recordTask.IsCompleted || recordTask.IsFaulted || recordTask.IsCanceled)
                            {
                                Fail(
                                    ExitRoundTransition,
                                    "MATCH_RECORD_FAILED",
                                    describeTaskFailure(recordTask));
                                yield return Finish();
                                yield break;
                            }

                            LocalMatchSnapshot after = recordTask.Result;
                            _lastMatch = after;
                            if (!_roundGuard.CommitTerminal(terminal, after, out string commitCode, out string commitDetail))
                            {
                                Fail(ExitRoundTransition, commitCode, commitDetail);
                                yield return Finish();
                                yield break;
                            }

                            LogEvent(
                                "match_round_committed",
                                "\"roundId\":\"" + Json(terminal.RoundId) + "\"" +
                                ",\"records\":" + Num(after.Records.Count) +
                                ",\"finished\":" + Bool(after.Finished) +
                                ",\"nextOrdinal\":" + Num(after.Ordinal));

                            // Only the existing session owner closes the round channel.
                            Task<ShutdownResult> shutdownTask = composition.ShutdownAsync("MATCH_ROUND_DONE");
                            float shutdownDeadline = Time.realtimeSinceStartup + 20f;
                            while (!shutdownTask.IsCompleted && Time.realtimeSinceStartup < shutdownDeadline)
                            {
                                yield return null;
                            }

                            if (!shutdownTask.IsCompleted || shutdownTask.IsFaulted || shutdownTask.IsCanceled)
                            {
                                Fail(ExitRoundTransition, "ROUND_SHUTDOWN_FAILED", describeTaskFailure(shutdownTask));
                                yield return Finish();
                                yield break;
                            }

                            ShutdownResult shutdown = shutdownTask.Result;
                            if (shutdown.Outcome != ShutdownOutcome.Completed || shutdown.Quarantined)
                            {
                                Fail(
                                    ExitRoundTransition,
                                    "ROUND_SHUTDOWN_INCOMPLETE",
                                    "Session shutdown outcome=" + shutdown.Outcome + " quarantined=" + shutdown.Quarantined + ".");
                                yield return Finish();
                                yield break;
                            }

                            if (after.Finished)
                            {
                                if (!LocalFullMatchPolicy.ValidateMatchCompletion(
                                        after,
                                        _terminals,
                                        out string finalCode,
                                        out string finalDetail))
                                {
                                    Fail(ExitFinalConsistency, finalCode, finalDetail);
                                    yield return Finish();
                                    yield break;
                                }

                                RecordFinal(after);
                                LogEvent(
                                    "match_finished",
                                    "\"records\":" + Num(after.Records.Count) +
                                    ",\"rankings\":[" + Seats(after.RankingSeats) + "]" +
                                    ",\"finalScores\":[" + Scores(after.FinalScores) + "]" +
                                    ",\"riichiSticks\":" + Num(after.RiichiSticks) +
                                    ",\"finishReason\":\"" + Json(after.FinishReason) + "\"");
                                _completed = true;
                                finished = true;
                                roundFinished = true;
                                break;
                            }

                            Task<SessionCoordinator> openTask = composition.OpenLocalSessionAsync(CancellationToken.None);
                            float openDeadline = Time.realtimeSinceStartup + 30f;
                            while (!openTask.IsCompleted && Time.realtimeSinceStartup < openDeadline)
                            {
                                yield return null;
                            }

                            if (!openTask.IsCompleted || openTask.IsFaulted || openTask.IsCanceled)
                            {
                                Fail(ExitSession, "NEXT_ROUND_OPEN_FAILED", describeTaskFailure(openTask));
                                yield return Finish();
                                yield break;
                            }

                            SessionCoordinator next = openTask.Result;
                            float nextReady = Time.realtimeSinceStartup + SessionReadyTimeoutSeconds;
                            while (Time.realtimeSinceStartup < nextReady &&
                                   (next.Current.State != TableConnectionState.Ready ||
                                    next.Current.PlayerSnapshot == null))
                            {
                                yield return null;
                                next = composition.SessionCoordinator ?? next;
                            }

                            if (next.Current.State != TableConnectionState.Ready ||
                                next.Current.PlayerSnapshot == null)
                            {
                                Fail(ExitSession, "NEXT_ROUND_NOT_READY", "The next round session did not become Ready.");
                                yield return Finish();
                                yield break;
                            }

                            string nextRoundId = next.Current.PlayerSnapshot.RoundId ?? string.Empty;
                            if (string.IsNullOrEmpty(nextRoundId) ||
                                string.Equals(nextRoundId, terminal.RoundId, StringComparison.Ordinal))
                            {
                                Fail(
                                    ExitRoundTransition,
                                    "NEXT_ROUND_REUSED",
                                    "The next session kept the previous terminal round id '" + nextRoundId + "'.");
                                yield return Finish();
                                yield break;
                            }

                            roundClock = new SystemTableClientClock();
                            mapper = new PlayerTableProjectionMapper(roundClock);
                            intentFactory = new TableCommandIntentFactory(roundClock);
                            _sessionRecoveries = 0;
                            _lastObservedRoundId = string.Empty;
                            _lastObservedStateVersion = 0UL;
                            _lastProgressAt = Time.realtimeSinceStartup;
                            LogEvent(
                                "round_started",
                                "\"roundId\":\"" + Json(nextRoundId) + "\"" +
                                ",\"stateVersion\":" + Num(next.Current.PlayerSnapshot.StateVersion));
                            roundFinished = true;
                            break;
                        }

                        // Action planning must use a fresh view after every state publication.
                        view = mapper.Project(coordinator.Current);
                        FullMatchStep step = PlanStep(coordinator.Current, view, intentFactory);
                        if (step.Kind == FullMatchStepKind.Wait)
                        {
                            yield return null;
                            continue;
                        }

                        if (step.Kind == FullMatchStepKind.Failed)
                        {
                            Fail(ExitNoDecision, step.Code, step.Detail);
                            yield return Finish();
                            yield break;
                        }

                        if (step.Kind == FullMatchStepKind.SubmitAction ||
                            step.Kind == FullMatchStepKind.SubmitMandatoryDraw)
                        {
                            FullMatchActionPlan plan = step.Plan!;
                            if (!_requestLedger.Record(plan, out string existing))
                            {
                                Fail(
                                    ExitActionUnconfirmed,
                                    "REQUEST_ID_CONFLICT",
                                    "The same logical action was issued two different request ids ('" +
                                    existing + "' vs '" + plan.RequestId + "').");
                                yield return Finish();
                                yield break;
                            }

                            LogEvent(
                                "action_plan",
                                "\"roundId\":\"" + Json(plan.RoundId) + "\"" +
                                ",\"decisionId\":\"" + Json(plan.DecisionId) + "\"" +
                                ",\"action\":\"" + plan.Action + "\"" +
                                ",\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                ",\"baselineStateVersion\":" + Num(plan.BaselineStateVersion) +
                                ",\"candidates\":[" + CandidateActions(view) + "]" +
                                ",\"requiresVisibleEvent\":" + Bool(plan.RequiresVisibleEvent));

                            var observer = new ConfirmedVisibleEventObserver(coordinator);
                            coordinator.ReceiptResolved += observer.OnReceiptResolved;
                            TableReceiptResolvedEventArgs? driverReceipt = null;
                            EventHandler<TableReceiptResolvedEventArgs> driverReceiptHandler = (_, args) =>
                            {
                                if (string.Equals(args.Command.RequestId, plan.RequestId, StringComparison.Ordinal) &&
                                    string.Equals(args.Command.ScopeId, plan.ScopeId, StringComparison.Ordinal))
                                {
                                    driverReceipt = args;
                                }
                            };
                            coordinator.ReceiptResolved += driverReceiptHandler;
                            try
                            {
                                observer.BeginAction(
                                    coordinator.Current,
                                    plan.RequestId,
                                    plan.ScopeId,
                                    plan.Action);

                                Task<CommandSubmission> submit = coordinator.SubmitAsync(plan.Command, CancellationToken.None);
                                float submitDeadline = Time.realtimeSinceStartup + SubmitTimeoutSeconds;
                                while (!submit.IsCompleted && Time.realtimeSinceStartup < submitDeadline)
                                {
                                    yield return null;
                                }

                                if (!submit.IsCompleted)
                                {
                                    _requestLedger.MarkUnresolved(plan);
                                    Fail(
                                        ExitActionUnconfirmed,
                                        "SUBMIT_TIMEOUT",
                                        "The original request '" + plan.RequestId +
                                        "' did not resolve; it will not be retried under a new id.");
                                    yield return Finish();
                                    yield break;
                                }

                                if (submit.IsFaulted || submit.IsCanceled || !submit.Result.Sent)
                                {
                                    _requestLedger.MarkUnresolved(plan);
                                    Fail(
                                        ExitActionUnconfirmed,
                                        "SUBMIT_NOT_SENT",
                                        describeTaskFailure(submit));
                                    yield return Finish();
                                    yield break;
                                }

                                bool confirmed = false;
                                bool applied = false;
                                float confirmDeadline = Time.realtimeSinceStartup + ConfirmationTimeoutSeconds;
                                while (Time.realtimeSinceStartup < confirmDeadline)
                                {
                                    ConfirmedVisibleActionEvent? confirmedEvent = observer.TryGetConfirmation();
                                    if (confirmedEvent != null &&
                                        LocalFullMatchPolicy.IsOwnActionEvent(
                                            plan,
                                            confirmedEvent.Batch,
                                            confirmedEvent.Event,
                                            confirmedEvent.CommandVersion))
                                    {
                                        confirmed = true;
                                        break;
                                    }

                                    if (!confirmed &&
                                        plan.Action == PlayerActionKind.DrawTile &&
                                        LocalFullMatchPolicy.TryFindOwnVisibleEvent(
                                            plan,
                                            observer.Batches,
                                            out ObservedVisibleEventBatch? fallbackBatch,
                                            out ObservedVisibleEvent? fallbackEvent))
                                    {
                                        // The mandatory draw has no server decision; the
                                        // client outbox can resolve before the UI dispatch
                                        // callback. Its own TileDrawn fact under this exact
                                        // binding/round/seat is still the authoritative
                                        // confirmed event, never another seat/type.
                                        confirmed = true;
                                        LogEvent(
                                            "visible_event_without_receipt",
                                            "\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                            ",\"action\":\"" + plan.Action + "\"" +
                                            ",\"event\":\"" + fallbackEvent!.Type + "\"" +
                                            ",\"eventSeq\":" + Num(fallbackEvent.EventSeq) +
                                            ",\"commandVersion\":" + Num(fallbackEvent.CommandVersion) +
                                            ",\"stateVersion\":" + Num(fallbackBatch!.StateVersion));
                                        break;
                                    }

                                    if (!confirmed && plan.Action == PlayerActionKind.DrawTile)
                                    {
                                        PlayerTableViewState drawView = mapper.Project(coordinator.Current);
                                        if (drawView.Identity.StateVersion > plan.BaselineStateVersion &&
                                            drawView.Own != null &&
                                            drawView.Own.DrawnTileId.HasValue &&
                                            drawView.Board.CurrentSeat == plan.Seat &&
                                            (drawView.Board.Phase == PlayerGamePhase.CheatWindow ||
                                             drawView.Board.Phase == PlayerGamePhase.Discard))
                                        {
                                            // The mandatory draw is a no-choice transition. The
                                            // confirmed post-state (new state version plus the
                                            // owner's drawn tile and turn) is the authoritative
                                            // visible result even if the receipt callback was lost.
                                            confirmed = true;
                                            LogEvent(
                                                "draw_confirmed_by_state",
                                                "\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                                ",\"stateVersion\":" + Num(drawView.Identity.StateVersion) +
                                                ",\"phase\":\"" + drawView.Board.Phase + "\"");
                                            break;
                                        }
                                    }

                                    if (observer.HasAppliedReceipt && !plan.RequiresVisibleEvent)
                                    {
                                        // The only legal state-only action is a response Pass.
                                        applied = true;
                                        break;
                                    }

                                    yield return null;
                                }

                                if (!confirmed && !applied)
                                {
                                    if (driverReceipt != null)
                                    {
                                        LogEvent(
                                            "action_receipt",
                                            "\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                            ",\"status\":\"" + driverReceipt.Resolution.ReceiptStatus + "\"" +
                                            ",\"reason\":\"" + Json(driverReceipt.Resolution.ReasonCode) + "\"" +
                                            ",\"stateVersion\":" + Num(driverReceipt.Resolution.EvaluatedStateVersion.Value));
                                    }

                                    if (driverReceipt != null &&
                                        driverReceipt.Resolution.ReceiptStatus == ReceiptStatus.Rejected &&
                                        IsReplanableRejection(driverReceipt.Resolution.ReasonCode))
                                    {
                                        // A rejected request is a definitive no-op. The
                                        // server decision that produced it cannot be
                                        // submitted again under the same id; wait for the
                                        // next confirmed decision/state. This never reuses
                                        // or overwrites an unknown request id.
                                        _requestLedger.MarkResolved(plan);
                                        if (plan.DecisionId.Length > 0)
                                        {
                                            _lastCompletedDecisionKey = plan.RoundId + "|" + plan.DecisionId;
                                        }
                                        else
                                        {
                                            _lastCompletedDrawKey = plan.RoundId + "|" +
                                                                   plan.BaselineStateVersion.ToString(CultureInfo.InvariantCulture);
                                        }

                                        LogEvent(
                                            "action_replanned",
                                            "\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                            ",\"action\":\"" + plan.Action + "\"" +
                                            ",\"reason\":\"" + Json(driverReceipt.Resolution.ReasonCode) + "\"");
                                        yield return null;
                                        continue;
                                    }

                                    if (driverReceipt != null &&
                                        driverReceipt.Resolution.ReceiptStatus != ReceiptStatus.Applied)
                                    {
                                        _requestLedger.MarkResolved(plan);
                                        Fail(
                                            ExitActionUnconfirmed,
                                            "ACTION_REJECTED",
                                            "Request '" + plan.RequestId + "' action=" + plan.Action +
                                            " was rejected: status=" + driverReceipt.Resolution.ReceiptStatus +
                                            " reason=" + driverReceipt.Resolution.ReasonCode + ".");
                                    }
                                    else if (observer.HasAppliedReceipt)
                                    {
                                        Fail(
                                            ExitMissingEvent,
                                            "VISIBLE_EVENT_MISSING",
                                            "Applied request '" + plan.RequestId + "' action=" + plan.Action +
                                            " has no matching visible event; " + observer.DescribeAction() +
                                            " " + observer.DescribeBatches());
                                    }
                                    else
                                    {
                                        _requestLedger.MarkUnresolved(plan);
                                        Fail(
                                            ExitActionUnconfirmed,
                                            "RECEIPT_UNRESOLVED",
                                            "No definitive Applied/rejected receipt arrived for request '" +
                                            plan.RequestId + "' action=" + plan.Action +
                                            " state=" + coordinator.Current.State +
                                            " lastError=" + (coordinator.Current.LastError ?? "<none>") +
                                            " pending=" + coordinator.Current.PendingCommands.Count +
                                            "; " + observer.DescribeAction());
                                    }

                                    yield return Finish();
                                    yield break;
                                }

                                _requestLedger.MarkResolved(plan);
                                if (plan.DecisionId.Length > 0)
                                {
                                    _lastCompletedDecisionKey = plan.RoundId + "|" + plan.DecisionId;
                                }
                                else
                                {
                                    _lastCompletedDrawKey = plan.RoundId + "|" +
                                                           plan.BaselineStateVersion.ToString(CultureInfo.InvariantCulture);
                                }

                                _actions.Add(new DriverActionRecord(
                                    plan.RoundId,
                                    plan.DecisionId,
                                    plan.Action.ToString(),
                                    plan.RequestId,
                                    confirmed));
                                RecordActionScenario(plan);
                                LogEvent(
                                    "action_confirmed",
                                    "\"roundId\":\"" + Json(plan.RoundId) + "\"" +
                                    ",\"action\":\"" + plan.Action + "\"" +
                                    ",\"requestId\":\"" + Json(plan.RequestId) + "\"" +
                                    ",\"visibleEvent\":" + Bool(confirmed) +
                                    ",\"stateVersion\":" + Num(coordinator.Current.PlayerSnapshot?.StateVersion ?? 0UL));
                            }
                            finally
                            {
                                coordinator.ReceiptResolved -= observer.OnReceiptResolved;
                                coordinator.ReceiptResolved -= driverReceiptHandler;
                                observer.Dispose();
                            }
                        }
                    }
                }

                yield return Finish();
        }

        private static bool IsReplanableRejection(string reasonCode)
        {
            return string.Equals(reasonCode, "STALE_STATE_VERSION", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "STALE_DECISION", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DECISION_MISMATCH", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ROUND_MISMATCH", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DEADLINE_EXPIRED", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "NOT_YOUR_TURN", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "SEAT_NOT_ELIGIBLE", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ROUND_FINISHED", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DECISION_ALREADY_OPEN", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ACTION_NOT_AVAILABLE", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ILLEGAL_ACTION", StringComparison.Ordinal);
        }

        private static string CandidateActions(PlayerTableViewState view)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(decision.Candidates[i].Action).Append('"');
            }

            return builder.ToString();
        }

        private FullMatchStep PlanStep(
            SessionViewState session,
            PlayerTableViewState view,
            TableCommandIntentFactory factory)
        {
            if (view.ConnectionState != TableConnectionState.Ready)
            {
                return FullMatchStep.Wait("session not Ready");
            }

            if (view.Board.Phase == PlayerGamePhase.RoundEnd)
            {
                return FullMatchStep.CompleteRound("confirmed ROUND_END");
            }

            if (view.Own?.Control?.ControlledByComputer == true)
            {
                return FullMatchStep.Wait("seat is under authoritative computer control");
            }

            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision != null)
            {
                string decisionKey = view.Identity.RoundId + "|" + decision.DecisionId;
                if (string.Equals(decisionKey, _lastCompletedDecisionKey, StringComparison.Ordinal))
                {
                    return FullMatchStep.Wait("the same server decision was already committed");
                }

                if (decision.Countdown.IsExpired(factory.NowMonotonicMilliseconds))
                {
                    return FullMatchStep.Failed(
                        "DEADLINE_EXPIRED",
                        "The server decision deadline expired before a legal action was submitted.");
                }

                if (LocalFullMatchPolicy.TryCreateActionPlan(
                        session,
                        view,
                        factory,
                        factory.NowMonotonicMilliseconds,
                        out FullMatchActionPlan? plan,
                        out string code,
                        out string detail))
                {
                    return FullMatchStep.SubmitAction(plan!);
                }

                return FullMatchStep.Failed(code, detail);
            }

            if ((view.Board.Phase == PlayerGamePhase.Draw || view.Board.Phase == PlayerGamePhase.KanDraw) &&
                view.Board.CurrentSeat == view.Identity.Seat)
            {
                string drawKey = view.Identity.RoundId + "|" +
                                 view.Identity.StateVersion.ToString(CultureInfo.InvariantCulture);
                if (string.Equals(drawKey, _lastCompletedDrawKey, StringComparison.Ordinal))
                {
                    return FullMatchStep.Wait("the same mandatory draw was already committed");
                }

                if (LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                        session,
                        view,
                        factory.NowMonotonicMilliseconds,
                        out FullMatchActionPlan? draw,
                        out string drawCode,
                        out string drawDetail))
                {
                    return FullMatchStep.SubmitMandatoryDraw(draw!);
                }

                return FullMatchStep.Failed(drawCode, drawDetail);
            }

            return FullMatchStep.Wait("no own decision");
        }

        private void RecordScenarios(FullMatchRoundTerminal terminal)
        {
            for (int i = 0; i < terminal.SettlementEventTypes.Count; i++)
            {
                string type = terminal.SettlementEventTypes[i];
                if (type == "TsumoWin" || type == "RonWin" || type == "DrawGame")
                {
                    if (!_observedScenarios.Contains(type))
                    {
                        _observedScenarios.Add(type);
                    }
                }
            }

            string outcome = terminal.Outcome;
            if (outcome == "Tsumo" || outcome == "Ron" || outcome == "Draw")
            {
                if (!_observedScenarios.Contains(outcome + "Outcome"))
                {
                    _observedScenarios.Add(outcome + "Outcome");
                }
            }
        }

        private void RecordActionScenario(FullMatchActionPlan plan)
        {
            string name = plan.Action switch
            {
                PlayerActionKind.DeclarePon => "PonDeclared",
                PlayerActionKind.DeclareChi => "ChiDeclared",
                PlayerActionKind.DeclareRiichi => "RiichiDeclared",
                PlayerActionKind.DeclareKan => "KanDeclared",
                PlayerActionKind.DeclareTsumo => "TsumoWin",
                PlayerActionKind.DeclareRon => "RonWin",
                PlayerActionKind.Pass => "Pass",
                PlayerActionKind.DrawTile => "Draw",
                _ => "Discard"
            };
            if (!_observedScenarios.Contains(name))
            {
                _observedScenarios.Add(name);
            }
        }

        private void RecordFinal(LocalMatchSnapshot match)
        {
            LogEvent(
                "final_facts",
                "\"finalScores\":[" + Scores(match.FinalScores) + "]" +
                ",\"rankings\":[" + Seats(match.RankingSeats) + "]" +
                ",\"riichiSticks\":" + Num(match.RiichiSticks) +
                ",\"records\":[" + Records(match.Records) + "]");
        }

        private IEnumerator Finish()
        {
            if (_finishStarted)
            {
                yield break;
            }

            _finishStarted = true;
            if (_finishedAt.Length == 0)
            {
                _finishedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            }

            int exitCode = _completed ? 0 : (_failed ? _failureExit : ExitUnexpected);
            string outcome = _completed ? "PASS" : (_failed ? "FAIL" : "FAIL");
            WriteSummary(exitCode, outcome);
            LogEvent(
                "driver_end",
                "\"outcome\":\"" + outcome + "\"" +
                ",\"exitCode\":" + Num(exitCode) +
                ",\"failureCode\":\"" + Json(_failureCode) + "\"" +
                ",\"failureDetail\":\"" + Json(_failureDetail) + "\"");
            if (_log != null)
            {
                _log.Flush();
                _log.Dispose();
                _log = null;
            }

            AppComposition? composition = AppRoot.Instance != null ? AppRoot.Instance.Composition : null;
            if (composition != null)
            {
                Task<ShutdownResult> shutdown = composition.ShutdownAsync("FULL_MATCH_DRIVER_END");
                float deadline = Time.realtimeSinceStartup + 15f;
                while (!shutdown.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (shutdown.IsCompleted && !shutdown.IsFaulted && !shutdown.IsCanceled)
                {
                    ShutdownResult result = shutdown.Result;
                    Debug.Log(
                        "FULL_MATCH_DRIVER_SHUTDOWN: Outcome=" + result.Outcome +
                        " Quarantined=" + result.Quarantined);
                }
            }

            yield return null;
            Debug.Log(
                "FULL_MATCH_DRIVER_RESULT: outcome=" + outcome +
                " exit=" + exitCode +
                " failure=" + (_failureCode.Length == 0 ? "none" : _failureCode));
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(exitCode);
#endif
        }

        private void Fail(int exitCode, string code, string detail)
        {
            if (_completed || _failed || _failureCode.Length > 0)
            {
                return;
            }

            _failed = true;
            _failureExit = exitCode;
            _failureCode = code ?? "UNKNOWN";
            _failureDetail = detail ?? string.Empty;
            Debug.LogError("FULL_MATCH_DRIVER_FAILED: code=" + _failureCode + " exit=" + exitCode + " detail=" + _failureDetail);
            LogEvent(
                "failure",
                "\"code\":\"" + Json(_failureCode) + "\"" +
                ",\"exitCode\":" + Num(exitCode) +
                ",\"detail\":\"" + Json(_failureDetail) + "\"");
        }

        private static string describeTaskFailure(Task task)
        {
            if (task.IsCanceled)
            {
                return "task cancelled";
            }

            if (task.IsFaulted && task.Exception != null)
            {
                return task.Exception.GetBaseException().GetType().Name + ": " +
                       task.Exception.GetBaseException().Message;
            }

            return task.IsCompleted ? "task incomplete" : "task did not complete";
        }

        private IEnumerator WaitForScene(AppRouter router, SceneId scene, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (router.CurrentScene != scene && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        private bool Click(string objectName)
        {
            GameObject? target = GameObject.Find(objectName);
            if (target == null)
            {
                return false;
            }

            UiInputLifecycle.Instance.OnResume();
            UiInputLifecycle.Instance.OnFocusGained();
            var data = new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                eligibleForClick = true
            };
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
            return true;
        }

        private void LogEvent(string kind, string fields)
        {
            if (_log == null)
            {
                return;
            }

            var builder = new StringBuilder(256);
            builder.Append("{\"at\":\"").Append(Json(DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)))
                .Append("\",\"kind\":\"").Append(Json(kind)).Append('"');
            if (!string.IsNullOrEmpty(fields))
            {
                builder.Append(',').Append(fields);
            }

            builder.Append('}');
            _log.WriteLine(builder.ToString());
        }

        private void WriteSummary(int exitCode, string outcome)
        {
            if (string.IsNullOrEmpty(_evidenceDir))
            {
                return;
            }

            var builder = new StringBuilder(4096);
            builder.Append("{\n");
            builder.Append("  \"schemaVersion\": 1,\n");
            builder.Append("  \"kind\": \"p45-full-match-driver-summary\",\n");
            builder.Append("  \"driverVersion\": \"").Append(Json(DriverVersion)).Append("\",\n");
            builder.Append("  \"outcome\": \"").Append(Json(outcome)).Append("\",\n");
            builder.Append("  \"exitCode\": ").Append(Num(exitCode)).Append(",\n");
            builder.Append("  \"failureCode\": \"").Append(Json(_failureCode)).Append("\",\n");
            builder.Append("  \"failureDetail\": \"").Append(Json(_failureDetail)).Append("\",\n");
            builder.Append("  \"finishedAt\": \"").Append(Json(_finishedAt)).Append("\",\n");
            builder.Append("  \"matchId\": \"").Append(Json(_lastMatch?.MatchId ?? string.Empty)).Append("\",\n");
            builder.Append("  \"matchFinished\": ").Append(Bool(_lastMatch?.Finished ?? false)).Append(",\n");
            builder.Append("  \"matchOrdinal\": ").Append(Num(_lastMatch?.Ordinal ?? 0)).Append(",\n");
            builder.Append("  \"finishReason\": \"").Append(Json(_lastMatch?.FinishReason ?? string.Empty)).Append("\",\n");
            builder.Append("  \"finalScores\": [").Append(_lastMatch == null ? string.Empty : Scores(_lastMatch.FinalScores)).Append("],\n");
            builder.Append("  \"rankings\": [").Append(_lastMatch == null ? string.Empty : Seats(_lastMatch.RankingSeats)).Append("],\n");
            builder.Append("  \"riichiSticks\": ").Append(Num(_lastMatch?.RiichiSticks ?? 0)).Append(",\n");
            builder.Append("  \"rounds\": [\n");
            for (int i = 0; i < _terminals.Count; i++)
            {
                FullMatchRoundTerminal terminal = _terminals[i];
                builder.Append("    {\"roundId\":\"").Append(Json(terminal.RoundId))
                    .Append("\",\"outcome\":\"").Append(Json(terminal.Outcome))
                    .Append("\",\"winnerSeat\":").Append(Num(terminal.WinnerSeat))
                    .Append(",\"loserSeat\":").Append(Num(terminal.LoserSeat))
                    .Append(",\"scores\":[").Append(Scores(terminal.FinalScores))
                    .Append("],\"riichiSticks\":").Append(Num(terminal.RemainingRiichiSticks))
                    .Append(",\"stateVersion\":").Append(Num(terminal.StateVersion))
                    .Append(",\"eventSeq\":").Append(Num(terminal.EventSeq))
                    .Append(",\"settlementEvents\":[").Append(Strings(terminal.SettlementEventTypes))
                    .Append("]}");
                if (i + 1 < _terminals.Count)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("  ],\n");
            builder.Append("  \"actions\": [\n");
            for (int i = 0; i < _actions.Count; i++)
            {
                DriverActionRecord action = _actions[i];
                builder.Append("    {\"roundId\":\"").Append(Json(action.RoundId))
                    .Append("\",\"decisionId\":\"").Append(Json(action.DecisionId))
                    .Append("\",\"action\":\"").Append(Json(action.Action))
                    .Append("\",\"requestId\":\"").Append(Json(action.RequestId))
                    .Append("\",\"visibleEvent\":").Append(Bool(action.VisibleEvent))
                    .Append('}');
                if (i + 1 < _actions.Count)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("  ],\n");
            builder.Append("  \"observedScenarios\": [").Append(Strings(_observedScenarios)).Append("],\n");
            builder.Append("  \"notRunScenarios\": [\"SleeveBloom\",\"BeamSwap\",\"TileMark\",\"ShufflePreparation\",\"AiTakeoverClaim\",\"Spectator\",\"RecoveryCompare\"]\n");
            builder.Append("}\n");
            string path = Path.Combine(_evidenceDir, "summary.json");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static string Scores(IReadOnlyList<long> values)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(Num(values[i]));
            }

            return builder.ToString();
        }

        private static string Scores(IEnumerable<Mahjong.Contracts.Wire.ScoreView> values)
        {
            var builder = new StringBuilder();
            bool first = true;
            foreach (Mahjong.Contracts.Wire.ScoreView value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(Num(value.Score));
                first = false;
            }

            return builder.ToString();
        }

        private static string Seats(IReadOnlyList<int> rankings)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < rankings.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(Num(rankings[i]));
            }

            return builder.ToString();
        }

        private static string Records(IReadOnlyList<LocalMatchRoundRecord> records)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < records.Count; i++)
            {
                LocalMatchRoundRecord record = records[i];
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"roundId\":\"").Append(Json(record.RoundId))
                    .Append("\",\"outcome\":\"").Append(Json(record.OutcomeCode))
                    .Append("\",\"scores\":[").Append(Scores(record.FinalScores))
                    .Append("],\"riichiSticks\":").Append(Num(record.RemainingRiichiSticks))
                    .Append('}');
            }

            return builder.ToString();
        }

        private static string Strings(IReadOnlyList<string> values)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(Json(values[i])).Append('"');
            }

            return builder.ToString();
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Json(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value!.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private sealed class DriverActionRecord
        {
            public DriverActionRecord(
                string roundId,
                string decisionId,
                string action,
                string requestId,
                bool visibleEvent)
            {
                RoundId = roundId;
                DecisionId = decisionId;
                Action = action;
                RequestId = requestId;
                VisibleEvent = visibleEvent;
            }

            public string RoundId { get; }
            public string DecisionId { get; }
            public string Action { get; }
            public string RequestId { get; }
            public bool VisibleEvent { get; }
        }
    }
}
