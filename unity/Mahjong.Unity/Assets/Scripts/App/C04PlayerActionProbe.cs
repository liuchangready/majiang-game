#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
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
            if (candidate.ControlEpoch.HasValue == false || view.Control == null || view.Control.ControlledByComputer ||
                view.Control.ControlEpoch != candidate.ControlEpoch.Value)
            {
                Fail("BASELINE_CONTROL_EPOCH_MISMATCH", "The legal candidate control epoch is not the seat-0 confirmed Player control baseline.");
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
            TableReceiptResolvedEventArgs? actualReceipt = null;
            EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => { actualReceipt = args; observer.RecordReceipt(args); };
            coordinator.ReceiptResolved += receiptHandler;
            observer.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, candidate.Action);
            string payloadHash = Sha256(intent.Command.Payload.CopyBytes());
            string payloadBase64 = Convert.ToBase64String(intent.Command.Payload.CopyBytes());
            CanonicalPayloadDocument actionPayload = CanonicalPayloadParser.Parse(intent.Command.Payload);
            if (actionPayload.CommandKind != (ushort)CommandKind.SubmitAction ||
                ReadCanonicalString(actionPayload, CommandPayloadFields.RoundIdField, "roundId") != view.Identity.RoundId ||
                ReadCanonicalString(actionPayload, CommandPayloadFields.DecisionIdField, "decisionId") != candidate.DecisionId ||
                actionPayload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch") != (candidate.ControlEpoch ?? 0UL) ||
                actionPayload.RequireUInt32(CommandPayloadFields.ActionType, "actionType") != (uint)ActionForCase(_caseId))
            {
                coordinator.ReceiptResolved -= receiptHandler;
                Fail("CANONICAL_ACTION_MISMATCH", "The encoded production command does not match the fixed candidate and case.");
                yield break;
            }
            string baselineRound = view.Identity.RoundId;
            string baselineTableId = coordinator.Current.Binding?.TableId ?? string.Empty;
            ulong baselineVersion = view.Identity.StateVersion;
            ulong baselineEventSeq = coordinator.Current.PlayerSnapshot!.EventSeq;
            ulong baselineCursor = coordinator.Current.Cursor?.ProjectionSeq.Value ?? 0UL;
            ulong baselineCursorStream = coordinator.Current.Cursor?.StreamVersion.Value ?? 0UL;
            Task<LocalTableRecoveryCapture> baselineAuthorityTask = LocalTableHost.CaptureRecoveryAsync(
                ((LocalTableChannelFactory)composition.ChannelFactory).SaveDirectory, CancellationToken.None);
            float authorityDeadline = Time.realtimeSinceStartup + 20f;
            while (!baselineAuthorityTask.IsCompleted && Time.realtimeSinceStartup < authorityDeadline) yield return null;
            if (!baselineAuthorityTask.IsCompleted || baselineAuthorityTask.IsFaulted || baselineAuthorityTask.IsCanceled)
            {
                Fail("BASELINE_AUTHORITY_CAPTURE_FAILED", "Could not read the real LocalHost checkpoint for the accepted Player baseline.");
                yield break;
            }
            LocalTableRecoveryFacts baselineAuthority = baselineAuthorityTask.Result.Facts;
            if (baselineAuthority.RoundId != baselineRound || baselineAuthority.StateVersion != baselineVersion.ToString() ||
                baselineAuthority.EventSeq != baselineEventSeq.ToString() || baselineAuthority.Physical.TileCount != 136 ||
                baselineAuthority.Physical.UniqueCount != 136)
            {
                Fail("BASELINE_AUTHORITY_DIVERGED", "The durable LocalHost physical checkpoint did not match the frozen Player baseline and 136-tile inventory.");
                yield break;
            }
            byte[] coordinatorBaselineBytes = coordinator.Current.PlayerSnapshot!.ToByteArray();
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
            ObservedVisibleEventBatch confirmedBatch = confirmation.Batch;
            StreamCursor? confirmedCursor = finalState.Cursor;
            if (!observer.HasAppliedReceipt || confirmedBatch.Kind != TableVisibleEventBatchKind.Incremental ||
                confirmedBatch.StateVersion <= baselineVersion ||
                confirmedBatch.EventSeq < confirmation.Event.EventSeq ||
                confirmation.ReceiptEventSeq < confirmation.Event.EventSeq ||
                confirmation.Event.CommandVersion != confirmation.CommandVersion ||
                confirmation.CommandVersion <= baselineVersion ||
                confirmedBatch.Seat != 0 || confirmedBatch.Role != ProtocolRole.Player ||
                confirmedBatch.BindingEpoch != view.Identity.BindingEpoch ||
                confirmedBatch.OwnerEpoch != view.Identity.OwnerEpoch ||
                confirmedBatch.ScopeId != view.Identity.ScopeId ||
                confirmedBatch.SessionId != view.Identity.SessionId ||
                confirmedBatch.RoomId != view.Identity.RoomId ||
                confirmedBatch.RoundId != baselineRound ||
                confirmedBatch.EventSeq <= baselineEventSeq ||
                !confirmedCursor.HasValue ||
                (confirmedCursor.Value.StreamVersion.Value == baselineCursorStream &&
                 confirmedCursor.Value.ProjectionSeq.Value <= baselineCursor) ||
                finalState.PlayerSnapshot == null ||
                finalState.PlayerSnapshot.StateVersion != confirmedBatch.StateVersion ||
                finalState.PlayerSnapshot.EventSeq != confirmedBatch.EventSeq)
            {
                Fail("ACTION_FACTS_DIVERGED", "Receipt, incremental event batch, baseline identity, and same-publication snapshot did not agree.");
                yield break;
            }

            Task<LocalTableRecoveryCapture> afterAuthorityTask = LocalTableHost.CaptureRecoveryAsync(
                ((LocalTableChannelFactory)composition.ChannelFactory).SaveDirectory, CancellationToken.None);
            authorityDeadline = Time.realtimeSinceStartup + 20f;
            while (!afterAuthorityTask.IsCompleted && Time.realtimeSinceStartup < authorityDeadline) yield return null;
            if (!afterAuthorityTask.IsCompleted || afterAuthorityTask.IsFaulted || afterAuthorityTask.IsCanceled)
            {
                Fail("AFTER_AUTHORITY_CAPTURE_FAILED", "Could not read the real committed LocalHost checkpoint after the accepted Player action.");
                yield break;
            }
            LocalTableRecoveryFacts afterAuthority = afterAuthorityTask.Result.Facts;
            if (afterAuthority.RoundId != baselineRound || afterAuthority.StateVersion != confirmedBatch.StateVersion.ToString() ||
                afterAuthority.EventSeq != confirmedBatch.EventSeq.ToString() || afterAuthority.Physical.TileCount != 136 ||
                afterAuthority.Physical.UniqueCount != 136)
            {
                Fail("AFTER_AUTHORITY_DIVERGED", "The durable post-action LocalHost checkpoint differs from the exact confirmed publication or violates physical 136/unique 136.");
                yield break;
            }

            uint[] expectedTiles = _caseId == "C04-G2-01" ? new uint[] { 0, 1, 2 } :
                _caseId == "C04-G2-02" ? new uint[] { 0, 4, 8 } :
                _caseId == "C04-G2-04" ? new uint[] { 0, 1, 2, 3 } : Array.Empty<uint>();
            if (expectedTiles.Length != 0 && !SameTiles(expectedTiles, confirmation.Event.Payload.TileIds))
            {
                Fail("ACTION_TILE_EFFECT_MISMATCH", "The typed action event does not carry the frozen physical tile set.");
                yield break;
            }

            PlayerTableViewState afterView = mapper.Project(finalState);
            ulong scoreBefore = unchecked((ulong)Math.Max(0L, ScoreForSeat(view.Board.Scores, 0)));
            ulong scoreAfter = unchecked((ulong)Math.Max(0L, ScoreForSeat(afterView.Board.Scores, 0)));
            if (_caseId == "C04-G2-01" || _caseId == "C04-G2-02")
            {
                PlayerMeldKind kind = _caseId == "C04-G2-01" ? PlayerMeldKind.Pon : PlayerMeldKind.Chi;
                PlayerMeldState? meld = null;
                for (int i = 0; i < afterView.OwnMelds.Count; i++)
                    if (afterView.OwnMelds[i].Kind == kind) { meld = afterView.OwnMelds[i]; break; }
                if (meld == null || !SameTiles(expectedTiles, meld.TileIds) || afterView.Own == null || ContainsAny(afterView.Own.ClosedTiles, expectedTiles))
                {
                    Fail("MELD_ENTITY_EFFECT_MISMATCH", "The same-publication owner view does not place the exact fixed physical entities in the claimed meld.");
                    yield break;
                }
            }
            else if (_caseId == "C04-G2-03")
            {
                if (!finalState.PlayerSnapshot.PublicView.Board.Players[0].Riichi ||
                    finalState.PlayerSnapshot.PublicView.Board.RiichiSticks != view.Board.RiichiSticks + 1 ||
                    ScoreForSeat(afterView.Board.Scores, 0) != 24_000L || scoreBefore != 25_000UL)
                {
                    Fail("RIICHI_EFFECT_MISMATCH", "The accepted snapshot does not show the 1,000-point deduction and riichi stick.");
                    yield break;
                }
            }
            else if (_caseId == "C04-G2-04")
            {
                if (afterView.Own?.DrawnTileId != 73u || afterView.OwnMelds.Count == 0 ||
                    afterView.OwnMelds[0].Kind != PlayerMeldKind.ClosedKan ||
                    !SameTiles(expectedTiles, afterView.OwnMelds[0].TileIds) ||
                    ContainsAny(afterView.Own!.ClosedTiles, expectedTiles) ||
                    afterAuthority.Physical.DeadWallCount != baselineAuthority.Physical.DeadWallCount - 1)
                {
                    Fail("KAN_ENTITY_EFFECT_MISMATCH", "Closed Kan did not move tiles 0..3 into the meld and entity 73 into the replacement draw.");
                    yield break;
                }
            }
            _outcome = "PASS";
            _exitCode = 0;
            if (actualReceipt == null || actualReceipt.Resolution.Disposition != OutboxDisposition.Applied ||
                actualReceipt.Command.RequestId != requestId || actualReceipt.Command.ScopeId != view.Identity.ScopeId ||
                actualReceipt.Resolution.CommandVersion?.Value != confirmation.CommandVersion ||
                actualReceipt.Resolution.EventSeq?.Value != confirmation.ReceiptEventSeq ||
                actualReceipt.Resolution.CanonicalPayloadSha256 != payloadHash)
            {
                Fail("RAW_RECEIPT_MISMATCH", "The captured typed receipt does not bind the exact request, scope, payload and observed command publication.");
                yield break;
            }

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
                TableId = baselineTableId,
                Seat = view.Identity.Seat,
                RoundId = baselineRound,
                DecisionId = candidate.DecisionId,
                ControlEpoch = candidate.ControlEpoch ?? 0UL,
                BindingEpoch = view.Identity.BindingEpoch,
                ConnectionEpoch = view.Identity.ConnectionEpoch,
                OwnerEpoch = view.Identity.OwnerEpoch,
                StateVersionBefore = baselineVersion,
                StateVersionAfter = confirmedBatch.StateVersion,
                EventSeqBefore = baselineEventSeq,
                EventSeqAfter = confirmedBatch.EventSeq,
                CursorBefore = baselineCursor,
                CursorAfter = confirmedCursor!.Value.ProjectionSeq.Value,
                CursorStreamBefore = baselineCursorStream,
                CursorStreamAfter = confirmedCursor!.Value.StreamVersion.Value,
                Receipt = observer.ReceiptSummary,
                ReceiptStatus = observer.HasAppliedReceipt ? "Applied" : "Missing",
                ReceiptRequestId = requestId,
                ReceiptScopeId = view.Identity.ScopeId,
                ReceiptCommandVersion = observer.AppliedCommandVersion ?? 0UL,
                ReceiptEventSeq = confirmation.ReceiptEventSeq,
                EventType = confirmation.Event.Type.ToString(),
                EventSeat = confirmation.Event.Seat,
                EventRoundId = confirmation.Batch.RoundId,
                EventScopeId = confirmation.Batch.ScopeId,
                EventSessionId = confirmation.Batch.SessionId,
                EventRoomId = confirmation.Batch.RoomId,
                EventTableId = confirmation.Batch.TableId,
                EventBindingEpoch = confirmation.Batch.BindingEpoch,
                EventConnectionEpoch = confirmation.Batch.ConnectionEpoch,
                EventOwnerEpoch = confirmation.Batch.OwnerEpoch,
                EventRole = confirmation.Batch.Role.ToString(),
                EventCommandVersion = confirmation.CommandVersion,
                EventSeq = confirmation.Event.EventSeq,
                AcceptanceSequence = confirmedBatch.AcceptanceSequence,
                EventConfirmed = true,
                EventTiles = String.Join(",", confirmation.Event.Payload.TileIds),
                MeldTileIds = afterView.OwnMelds.Count == 0
                    ? string.Empty : String.Join(",", afterView.OwnMelds[0].TileIds),
                HandTileIdsAfter = afterView.Own == null ? string.Empty : String.Join(",", afterView.Own.ClosedTiles),
                ReplacementDrawTileId = afterView.Own?.DrawnTileId?.ToString() ?? string.Empty,
                SeatScoreBefore = scoreBefore,
                SeatScoreAfter = scoreAfter,
                RiichiSticksBefore = view.Board.RiichiSticks,
                RiichiSticksAfter = finalState.PlayerSnapshot.PublicView.Board.RiichiSticks,
                Batch = confirmation.Batch.Describe(),
                PayloadActionType = actionPayload.RequireUInt32(CommandPayloadFields.ActionType, "actionType"),
                PayloadRoundId = ReadCanonicalString(actionPayload, CommandPayloadFields.RoundIdField, "roundId"),
                PayloadDecisionId = ReadCanonicalString(actionPayload, CommandPayloadFields.DecisionIdField, "decisionId"),
                PayloadControlEpoch = actionPayload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"),
                CandidateOptionId = candidate.OptionId ?? string.Empty,
                PayloadOptionId = actionPayload.HasField(CommandPayloadFields.OptionId)
                    ? ReadCanonicalString(actionPayload, CommandPayloadFields.OptionId, "optionId") : string.Empty,
                PayloadMeldType = actionPayload.OptionalUInt32(CommandPayloadFields.MeldType) ?? 0U,
                PayloadTileIds = actionPayload.HasField(CommandPayloadFields.TileIds)
                    ? String.Join(",", actionPayload.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")) : string.Empty,
                Status = "GAME"
            };
            WriteActionEvidence(new C04PlayerActionEvidence
            {
                CaseId = _caseId, RequestId = requestId, ScopeId = view.Identity.ScopeId,
                PayloadBase64 = payloadBase64, PayloadSha256 = payloadHash,
                BaselineRoomId = view.Identity.RoomId, BaselineSessionId = view.Identity.SessionId,
                BaselineTableId = baselineTableId, BaselineRoundId = baselineRound, BaselineSeat = view.Identity.Seat,
                BaselineBindingEpoch = view.Identity.BindingEpoch, BaselineConnectionEpoch = view.Identity.ConnectionEpoch,
                BaselineOwnerEpoch = view.Identity.OwnerEpoch, BaselineControlEpoch = candidate.ControlEpoch ?? 0UL,
                BaselineDecisionId = candidate.DecisionId, BaselineStateVersion = baselineVersion,
                BaselineEventSeq = baselineEventSeq, BaselineCursorStream = baselineCursorStream, BaselineCursor = baselineCursor,
                BaselinePlayerSnapshotBase64 = Convert.ToBase64String(coordinatorBaselineBytes),
                BaselineAuthoritySaveBase64 = Convert.ToBase64String(baselineAuthorityTask.Result.AuthoritySaveBytes),
                BaselineAuthorityCheatStateBase64 = Convert.ToBase64String(baselineAuthorityTask.Result.CheatStatePayload),
                BaselinePhysicalTileCount = baselineAuthority.Physical.TileCount,
                BaselinePhysicalUniqueCount = baselineAuthority.Physical.UniqueCount,
                BaselineDeadWallCount = baselineAuthority.Physical.DeadWallCount,
                BaselinePhysicalTilesSha256 = baselineAuthority.Physical.TilesSha256,
                ReceiptDisposition = actualReceipt.Resolution.Disposition.ToString(),
                ReceiptStatus = actualReceipt.Resolution.ReceiptStatus.ToString(),
                ReceiptRequestId = actualReceipt.Command.RequestId, ReceiptScopeId = actualReceipt.Command.ScopeId,
                ReceiptDigest = actualReceipt.Resolution.CanonicalPayloadSha256,
                ReceiptCommandVersion = actualReceipt.Resolution.CommandVersion?.Value ?? 0UL,
                ReceiptEventSeq = actualReceipt.Resolution.EventSeq?.Value ?? 0UL,
                EventType = confirmation.Event.Type.ToString(), EventRoundId = confirmedBatch.RoundId,
                EventScopeId = confirmedBatch.ScopeId, EventSessionId = confirmedBatch.SessionId,
                EventRoomId = confirmedBatch.RoomId, EventTableId = confirmedBatch.TableId,
                EventSeat = confirmation.Event.Seat, EventBindingEpoch = confirmedBatch.BindingEpoch,
                EventConnectionEpoch = confirmedBatch.ConnectionEpoch, EventOwnerEpoch = confirmedBatch.OwnerEpoch,
                EventRole = confirmedBatch.Role.ToString(), EventBatchKind = confirmedBatch.Kind.ToString(),
                EventFullBaseline = confirmedBatch.FullBaseline, EventCommandVersion = confirmation.CommandVersion,
                EventSeq = confirmation.Event.EventSeq, EventTileIds = String.Join(",", confirmation.Event.Payload.TileIds),
                AcceptanceSequence = confirmedBatch.AcceptanceSequence,
                AfterRoomId = finalState.VisibleEventBatch.RoomId, AfterSessionId = finalState.VisibleEventBatch.SessionId,
                AfterTableId = finalState.VisibleEventBatch.TableId, AfterRoundId = finalState.VisibleEventBatch.RoundId,
                AfterSeat = finalState.VisibleEventBatch.Seat, AfterBindingEpoch = finalState.VisibleEventBatch.BindingEpoch,
                AfterConnectionEpoch = finalState.VisibleEventBatch.ConnectionEpoch, AfterOwnerEpoch = finalState.VisibleEventBatch.OwnerEpoch,
                AfterStateVersion = finalState.PlayerSnapshot.StateVersion, AfterEventSeq = finalState.PlayerSnapshot.EventSeq,
                AfterAcceptanceSequence = finalState.AcceptanceSequence,
                AfterCursorStream = confirmedCursor.Value.StreamVersion.Value, AfterCursor = confirmedCursor.Value.ProjectionSeq.Value,
                AfterPlayerSnapshotBase64 = Convert.ToBase64String(finalState.PlayerSnapshot.ToByteArray()),
                AfterAuthoritySaveBase64 = Convert.ToBase64String(afterAuthorityTask.Result.AuthoritySaveBytes),
                AfterAuthorityCheatStateBase64 = Convert.ToBase64String(afterAuthorityTask.Result.CheatStatePayload),
                AfterPhysicalTileCount = afterAuthority.Physical.TileCount,
                AfterPhysicalUniqueCount = afterAuthority.Physical.UniqueCount,
                AfterDeadWallCount = afterAuthority.Physical.DeadWallCount,
                AfterPhysicalTilesSha256 = afterAuthority.Physical.TilesSha256,
                HandTileIds = afterView.Own == null ? string.Empty : String.Join(",", afterView.Own.ClosedTiles),
                DrawnTileId = afterView.Own?.DrawnTileId?.ToString() ?? string.Empty,
                MeldKind = afterView.OwnMelds.Count == 0 ? string.Empty : afterView.OwnMelds[0].Kind.ToString(),
                MeldTileIds = afterView.OwnMelds.Count == 0 ? string.Empty : String.Join(",", afterView.OwnMelds[0].TileIds),
                SeatScoreBefore = scoreBefore, SeatScoreAfter = scoreAfter,
                RiichiSticksBefore = view.Board.RiichiSticks, RiichiSticksAfter = finalState.PlayerSnapshot.PublicView.Board.RiichiSticks
            });
            Finish();
            yield break;
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

        private void WriteActionEvidence(C04PlayerActionEvidence value)
        {
            if (string.IsNullOrEmpty(_evidence)) throw new InvalidOperationException("The owned raw evidence destination was not reserved.");
            using var stream = new FileStream(_evidence, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
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

        private static bool SameTiles(IReadOnlyList<uint> expected, IReadOnlyList<uint> actual)
        {
            if (expected.Count != actual.Count) return false;
            var left = new System.Collections.Generic.List<uint>(expected);
            var right = new System.Collections.Generic.List<uint>(actual);
            left.Sort(); right.Sort();
            for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool ContainsAny(IReadOnlyList<uint> actual, IReadOnlyList<uint> expected)
        {
            for (int i = 0; i < actual.Count; i++)
                for (int j = 0; j < expected.Count; j++)
                    if (actual[i] == expected[j]) return true;
            return false;
        }

        private static string ReadCanonicalString(CanonicalPayloadDocument document, ushort fieldNumber, string fieldName)
        {
            // Canonical String values carry an inner UInt32 byte length before UTF-8.
            // The currently accepted field view exposes that encoded prefix as text,
            // so decode the canonical bytes at this boundary without changing the shared contract.
            byte[] encoded = Encoding.UTF8.GetBytes(document.RequireString(fieldNumber, fieldName));
            if (encoded.Length < 4 || BitConverter.ToUInt32(encoded, 0) != encoded.Length - 4)
                throw new InvalidDataException("Canonical string field " + fieldName + " has an invalid inner length.");
            return new UTF8Encoding(false, true).GetString(encoded, 4, encoded.Length - 4);
        }

        private static long ScoreForSeat(IReadOnlyList<PlayerTableScore> scores, int seat)
        {
            for (int i = 0; i < scores.Count; i++) if (scores[i].Seat == seat) return scores[i].Score;
            throw new InvalidOperationException("The accepted player snapshot omitted seat " + seat + " score.");
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
            public string TableId = string.Empty;
            public int Seat = -1;
            public string RoundId = string.Empty;
            public string DecisionId = string.Empty;
            public ulong ControlEpoch;
            public ulong BindingEpoch;
            public ulong ConnectionEpoch;
            public ulong OwnerEpoch;
            public ulong StateVersionBefore;
            public ulong StateVersionAfter;
            public ulong EventSeqBefore;
            public ulong EventSeqAfter;
            public ulong CursorBefore;
            public ulong CursorAfter;
            public ulong CursorStreamBefore;
            public ulong CursorStreamAfter;
            public uint PayloadActionType;
            public string PayloadRoundId = string.Empty;
            public string PayloadDecisionId = string.Empty;
            public ulong PayloadControlEpoch;
            public string CandidateOptionId = string.Empty;
            public string PayloadOptionId = string.Empty;
            public uint PayloadMeldType;
            public string PayloadTileIds = string.Empty;
            public string Receipt = string.Empty;
            public string ReceiptStatus = string.Empty;
            public string ReceiptRequestId = string.Empty;
            public string ReceiptScopeId = string.Empty;
            public ulong ReceiptCommandVersion;
            public ulong ReceiptEventSeq;
            public string EventType = string.Empty;
            public int EventSeat = -1;
            public string EventRoundId = string.Empty;
            public string EventScopeId = string.Empty;
            public string EventSessionId = string.Empty;
            public string EventRoomId = string.Empty;
            public string EventTableId = string.Empty;
            public ulong EventBindingEpoch;
            public ulong EventConnectionEpoch;
            public ulong EventOwnerEpoch;
            public string EventRole = string.Empty;
            public ulong EventCommandVersion;
            public long AcceptanceSequence;
            public ulong EventSeq;
            public bool EventConfirmed;
            public string EventTiles = string.Empty;
            public string MeldTileIds = string.Empty;
            public string HandTileIdsAfter = string.Empty;
            public string ReplacementDrawTileId = string.Empty;
            public ulong SeatScoreBefore;
            public ulong SeatScoreAfter;
            public uint RiichiSticksBefore;
            public uint RiichiSticksAfter;
            public string Batch = string.Empty;
            public string Status = string.Empty;
            public string Failure = string.Empty;
        }

        [Serializable]
        private sealed class C04PlayerActionEvidence
        {
            public string CaseId = string.Empty, RequestId = string.Empty, ScopeId = string.Empty;
            public string PayloadBase64 = string.Empty, PayloadSha256 = string.Empty;
            public string BaselineRoomId = string.Empty, BaselineSessionId = string.Empty, BaselineTableId = string.Empty, BaselineRoundId = string.Empty;
            public int BaselineSeat = -1;
            public ulong BaselineBindingEpoch, BaselineConnectionEpoch, BaselineOwnerEpoch, BaselineControlEpoch, BaselineStateVersion, BaselineEventSeq, BaselineCursorStream, BaselineCursor;
            public string BaselineDecisionId = string.Empty, BaselinePlayerSnapshotBase64 = string.Empty;
            public string BaselineAuthoritySaveBase64 = string.Empty, BaselineAuthorityCheatStateBase64 = string.Empty;
            public int BaselinePhysicalTileCount, BaselinePhysicalUniqueCount, BaselineDeadWallCount;
            public string BaselinePhysicalTilesSha256 = string.Empty;
            public string ReceiptDisposition = string.Empty, ReceiptStatus = string.Empty, ReceiptRequestId = string.Empty, ReceiptScopeId = string.Empty, ReceiptDigest = string.Empty;
            public ulong ReceiptCommandVersion, ReceiptEventSeq;
            public string EventType = string.Empty, EventRoundId = string.Empty;
            public string EventScopeId = string.Empty, EventSessionId = string.Empty, EventRoomId = string.Empty, EventTableId = string.Empty;
            public int EventSeat = -1;
            public ulong EventBindingEpoch, EventConnectionEpoch, EventOwnerEpoch, EventCommandVersion, EventSeq;
            public string EventRole = string.Empty, EventBatchKind = string.Empty;
            public bool EventFullBaseline;
            public long AcceptanceSequence, AfterAcceptanceSequence;
            public string EventTileIds = string.Empty;
            public string AfterRoomId = string.Empty, AfterSessionId = string.Empty, AfterTableId = string.Empty, AfterRoundId = string.Empty;
            public int AfterSeat = -1;
            public ulong AfterBindingEpoch, AfterConnectionEpoch, AfterOwnerEpoch, AfterStateVersion, AfterEventSeq, AfterCursorStream, AfterCursor;
            public int AfterPhysicalTileCount, AfterPhysicalUniqueCount, AfterDeadWallCount;
            public string AfterPhysicalTilesSha256 = string.Empty;
            public string AfterPlayerSnapshotBase64 = string.Empty, AfterAuthoritySaveBase64 = string.Empty, AfterAuthorityCheatStateBase64 = string.Empty;
            public string HandTileIds = string.Empty, DrawnTileId = string.Empty, MeldKind = string.Empty, MeldTileIds = string.Empty;
            public ulong SeatScoreBefore, SeatScoreAfter;
            public uint RiichiSticksBefore, RiichiSticksAfter;
        }
    }
}
