#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.LocalHost;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using WirePlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Why the opt-in full-match driver must perform the next authoritative step.
    /// This type is intentionally free of Unity APIs so the real production policy
    /// can be compiled and tested without an Editor or Player.
    /// </summary>
    public enum FullMatchStepKind
    {
        Wait = 0,
        SubmitAction = 1,
        SubmitMandatoryDraw = 2,
        CompleteRound = 3,
        Finished = 4,
        Failed = 5
    }

    /// <summary>A single bounded driver decision derived from a confirmed view.</summary>
    public sealed class FullMatchStep
    {
        private FullMatchStep(
            FullMatchStepKind kind,
            FullMatchActionPlan? plan,
            string code,
            string detail)
        {
            Kind = kind;
            Plan = plan;
            Code = code ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public FullMatchStepKind Kind { get; }

        public FullMatchActionPlan? Plan { get; }

        public string Code { get; }

        public string Detail { get; }

        public static FullMatchStep Wait(string detail) =>
            new FullMatchStep(FullMatchStepKind.Wait, null, string.Empty, detail);

        public static FullMatchStep SubmitAction(FullMatchActionPlan plan) =>
            new FullMatchStep(FullMatchStepKind.SubmitAction, plan, string.Empty, string.Empty);

        public static FullMatchStep SubmitMandatoryDraw(FullMatchActionPlan plan) =>
            new FullMatchStep(FullMatchStepKind.SubmitMandatoryDraw, plan, string.Empty, string.Empty);

        public static FullMatchStep CompleteRound(string detail) =>
            new FullMatchStep(FullMatchStepKind.CompleteRound, null, string.Empty, detail);

        public static FullMatchStep Finished(string detail) =>
            new FullMatchStep(FullMatchStepKind.Finished, null, string.Empty, detail);

        public static FullMatchStep Failed(string code, string detail) =>
            new FullMatchStep(FullMatchStepKind.Failed, null, code, detail);
    }

    /// <summary>
    /// Immutable identity of one command attempt. The request id is derived only from
    /// authoritative snapshot fields plus the selected server candidate, so re-planning
    /// the same logical action returns the same id. An unknown result is never retried
    /// with a new id.
    /// </summary>
    public sealed class FullMatchActionPlan
    {
        internal FullMatchActionPlan(
            ProtocolCommand command,
            TableCommandIntent? intent,
            string requestId,
            PlayerActionKind action,
            string roundId,
            string decisionId,
            int seat,
            string sessionId,
            string roomId,
            string tableId,
            string scopeId,
            ulong bindingEpoch,
            ulong connectionEpoch,
            ulong ownerEpoch,
            ulong baselineStateVersion)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Intent = intent;
            RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
            Action = action;
            RoundId = roundId ?? string.Empty;
            DecisionId = decisionId ?? string.Empty;
            Seat = seat;
            SessionId = sessionId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            TableId = tableId ?? string.Empty;
            ScopeId = scopeId ?? string.Empty;
            BindingEpoch = bindingEpoch;
            ConnectionEpoch = connectionEpoch;
            OwnerEpoch = ownerEpoch;
            BaselineStateVersion = baselineStateVersion;
        }

        public ProtocolCommand Command { get; }

        /// <summary>Non-null for a server-candidate action; null for the mandatory draw.</summary>
        public TableCommandIntent? Intent { get; }

        public string RequestId { get; }

        public PlayerActionKind Action { get; }

        public string RoundId { get; }

        public string DecisionId { get; }

        public int Seat { get; }

        public string SessionId { get; }

        public string RoomId { get; }

        public string TableId { get; }

        public string ScopeId { get; }

        public ulong BindingEpoch { get; }

        public ulong ConnectionEpoch { get; }

        public ulong OwnerEpoch { get; }

        public ulong BaselineStateVersion { get; }

        public bool RequiresVisibleEvent => !SmokeActionEventPolicy.AllowsEmptyPublication(Action);

        public string LogicalKey =>
            RoundId + "|" + DecisionId + "|" + (int)Action + "|" +
            (Intent == null ? "draw" : Intent.Key.ToString());
    }

    /// <summary>Authoritative terminal facts copied from a confirmed round-end snapshot.</summary>
    public sealed class FullMatchRoundTerminal
    {
        public FullMatchRoundTerminal(
            string roundId,
            IReadOnlyList<long> finalScores,
            int remainingRiichiSticks,
            string outcome,
            int winnerSeat,
            int loserSeat,
            ulong stateVersion,
            ulong eventSeq,
            IReadOnlyList<string> settlementEventTypes)
        {
            RoundId = roundId ?? string.Empty;
            FinalScores = FullMatchCopy.Longs(finalScores);
            RemainingRiichiSticks = remainingRiichiSticks;
            Outcome = outcome ?? string.Empty;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            SettlementEventTypes = FullMatchCopy.Strings(settlementEventTypes);
        }

        public string RoundId { get; }

        public IReadOnlyList<long> FinalScores { get; }

        public int RemainingRiichiSticks { get; }

        public string Outcome { get; }

        public int WinnerSeat { get; }

        public int LoserSeat { get; }

        public ulong StateVersion { get; }

        public ulong EventSeq { get; }

        public IReadOnlyList<string> SettlementEventTypes { get; }

        public long TableTotal
        {
            get
            {
                long total = 0;
                for (int i = 0; i < FinalScores.Count; i++)
                {
                    total += FinalScores[i];
                }

                return total + (RemainingRiichiSticks * 1000L);
            }
        }
    }

    public enum FullMatchRoundTransition
    {
        NewRound = 0,
        InRound = 1,
        Terminal = 2,
        Wait = 3,
        Failed = 4
    }

    /// <summary>
    /// Fences round identity. A terminal round is never revived, a new round may
    /// only appear after the previous terminal was committed to the match, and a
    /// round id already completed can never become current again.
    /// </summary>
    public sealed class FullMatchRoundGuard
    {
        private readonly List<string> _completed = new List<string>();
        private readonly List<string> _seen = new List<string>();
        private string _current = string.Empty;
        private bool _terminalSeen;
        private bool _terminalCommitted;

        public string CurrentRoundId => _current;

        public bool TerminalSeen => _terminalSeen;

        public bool TerminalCommitted => _terminalCommitted;

        public int CompletedCount => _completed.Count;

        public IReadOnlyList<string> SeenRoundIds => _seen.AsReadOnly();

        public IReadOnlyList<string> CompletedRoundIds => _completed.AsReadOnly();

        public FullMatchRoundTransition Observe(
            PlayerTableViewState view,
            out string failureCode,
            out string detail)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            failureCode = string.Empty;
            detail = string.Empty;
            if (view.ConnectionState != TableConnectionState.Ready || view.Identity.RoundId.Length == 0)
            {
                return FullMatchRoundTransition.Wait;
            }

            string roundId = view.Identity.RoundId;
            if (_current.Length == 0)
            {
                SetRound(roundId);
                return FullMatchRoundTransition.NewRound;
            }

            if (!string.Equals(_current, roundId, StringComparison.Ordinal))
            {
                if (_completed.Contains(roundId))
                {
                    failureCode = "ROUND_REVIVED";
                    detail = "The client returned to an already completed round '" + roundId + "'.";
                    return FullMatchRoundTransition.Failed;
                }

                if (!_terminalSeen)
                {
                    failureCode = "ROUND_CHANGED_BEFORE_TERMINAL";
                    detail = "Round changed from '" + _current + "' to '" + roundId + "' before a terminal confirmation.";
                    return FullMatchRoundTransition.Failed;
                }

                if (!_terminalCommitted)
                {
                    failureCode = "NEXT_ROUND_BEFORE_COMMIT";
                    detail = "Round changed from '" + _current + "' to '" + roundId +
                             "' before the terminal result was committed to the match.";
                    return FullMatchRoundTransition.Failed;
                }

                SetRound(roundId);
                return FullMatchRoundTransition.NewRound;
            }

            if (view.Board.Phase == PlayerGamePhase.RoundEnd)
            {
                _terminalSeen = true;
                return FullMatchRoundTransition.Terminal;
            }

            if (_terminalSeen)
            {
                failureCode = _terminalCommitted ? "ROUND_REVIVED_AFTER_COMMIT" : "TERMINAL_ROUND_REVIVED";
                detail = "The terminal round '" + roundId + "' returned to an active phase before a new round.";
                return FullMatchRoundTransition.Failed;
            }

            return FullMatchRoundTransition.InRound;
        }

        /// <summary>
        /// Binds the terminal facts to the durable match record produced by the
        /// accepted LocalMatchCoordinator. The driver never computes a score, rank or
        /// next round itself.
        /// </summary>
        public bool CommitTerminal(
            FullMatchRoundTerminal terminal,
            LocalMatchSnapshot match,
            out string failureCode,
            out string detail)
        {
            if (terminal == null) throw new ArgumentNullException(nameof(terminal));
            if (match == null) throw new ArgumentNullException(nameof(match));
            failureCode = string.Empty;
            detail = string.Empty;
            if (!_terminalSeen || !string.Equals(_current, terminal.RoundId, StringComparison.Ordinal))
            {
                failureCode = "TERMINAL_NOT_CURRENT";
                detail = "The terminal facts do not belong to the current open round.";
                return false;
            }

            if (match.Records.Count != _completed.Count + 1)
            {
                failureCode = "MATCH_RECORD_COUNT";
                detail = "Expected " + (_completed.Count + 1) + " match round records, got " + match.Records.Count + ".";
                return false;
            }

            LocalMatchRoundRecord record = match.Records[match.Records.Count - 1];
            if (!string.Equals(record.RoundId, terminal.RoundId, StringComparison.Ordinal))
            {
                failureCode = "MATCH_RECORD_ROUND";
                detail = "The match committed round '" + record.RoundId + "' but the client terminal was '" + terminal.RoundId + "'.";
                return false;
            }

            if (!SameScores(record.FinalScores, terminal.FinalScores))
            {
                failureCode = "ROUND_SCORE_MISMATCH";
                detail = "The committed match scores do not equal the client's authoritative round settlement.";
                return false;
            }

            if (record.RemainingRiichiSticks != terminal.RemainingRiichiSticks)
            {
                failureCode = "ROUND_STICK_MISMATCH";
                detail = "The committed match riichi sticks do not equal the client's authoritative settlement.";
                return false;
            }

            if (_completed.Contains(terminal.RoundId))
            {
                failureCode = "ROUND_DOUBLE_COMMIT";
                detail = "The same terminal round was committed twice.";
                return false;
            }

            _completed.Add(terminal.RoundId);
            _terminalCommitted = true;
            return true;
        }

        private void SetRound(string roundId)
        {
            _current = roundId;
            _terminalSeen = false;
            _terminalCommitted = false;
            if (!_seen.Contains(roundId))
            {
                _seen.Add(roundId);
            }
        }

        internal static bool SameScores(IReadOnlyList<long> left, IReadOnlyList<long> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Remembers the one request id issued for a logical action. A re-plan of the
    /// same decision/action must reuse the original id; after an unknown outcome the
    /// driver may reconcile the original request but must never mint a replacement.
    /// </summary>
    public sealed class FullMatchRequestLedger
    {
        private readonly Dictionary<string, string> _issued =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _unresolved = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Returns false when the same logical action was issued another id.</summary>
        public bool Record(FullMatchActionPlan plan, out string existingRequestId)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            string key = plan.LogicalKey;
            if (_issued.TryGetValue(key, out string? existing))
            {
                existingRequestId = existing;
                return string.Equals(existing, plan.RequestId, StringComparison.Ordinal);
            }

            _issued.Add(key, plan.RequestId);
            existingRequestId = plan.RequestId;
            return true;
        }

        public void MarkUnresolved(FullMatchActionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            _unresolved.Add(plan.LogicalKey);
        }

        public bool IsUnresolved(FullMatchActionPlan plan)
        {
            return plan != null && _unresolved.Contains(plan.LogicalKey);
        }

        public void MarkResolved(FullMatchActionPlan plan)
        {
            if (plan != null)
            {
                _unresolved.Remove(plan.LogicalKey);
            }
        }

        /// <summary>
        /// An unknown outcome may be reconciled or re-sent with the original bytes,
        /// never re-created under a new id.
        /// </summary>
        public bool MayIssueNewRequestId(FullMatchActionPlan plan)
        {
            return plan != null && !IsUnresolved(plan);
        }
    }

    /// <summary>
    /// Pure full-match policy: candidate selection, stable request identity, strict
    /// confirmation matching, terminal capture and final completion cross-checks.
    /// It never reads Domain, TableActor, a save file or an opponent's hidden tiles.
    /// </summary>
    public static class LocalFullMatchPolicy
    {
        /// <summary>
        /// Server candidates only, in a fixed preference order. A declaration is
        /// preferred over a discard, and a mandatory no-choice draw is never invented
        /// here. The selected action always comes from the confirmed decision list.
        /// </summary>
        public static PlayerLegalActionState? SelectAction(PlayerTableViewState view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null || decision.Candidates.Count == 0)
            {
                return null;
            }

            PlayerActionKind[] priority =
            {
                PlayerActionKind.DeclareTsumo,
                PlayerActionKind.DeclareRon,
                PlayerActionKind.DiscardTile,
                PlayerActionKind.DrawTile,
                PlayerActionKind.Pass,
                PlayerActionKind.DeclareKan,
                PlayerActionKind.DeclareRiichi,
                PlayerActionKind.DeclarePon,
                PlayerActionKind.DeclareChi,
                PlayerActionKind.DeclareAbortiveDraw
            };

            for (int p = 0; p < priority.Length; p++)
            {
                for (int i = 0; i < decision.Candidates.Count; i++)
                {
                    PlayerLegalActionState candidate = decision.Candidates[i];
                    if (candidate.Action != priority[p])
                    {
                        continue;
                    }

                    // A riichi declaration is a discard-bearing action in the frozen
                    // protocol; a candidate without its confirmed tile cannot be
                    // converted into a legal payload and must not be guessed.
                    if (candidate.Action == PlayerActionKind.DeclareRiichi && candidate.TileIds.Count == 0)
                    {
                        continue;
                    }

                    return candidate;
                }
            }

            return null;
        }

        public static string BuildSubmitRequestId(
            string roundId,
            string decisionId,
            PlayerActionKind action,
            string optionId,
            IReadOnlyList<uint> tileIds)
        {
            var builder = new StringBuilder(160);
            builder.Append("submit|").Append(roundId ?? string.Empty).Append('|')
                .Append(decisionId ?? string.Empty).Append('|')
                .Append(((int)action).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(optionId ?? string.Empty).Append('|');
            for (int i = 0; i < tileIds.Count; i++)
            {
                builder.Append(tileIds[i].ToString(CultureInfo.InvariantCulture)).Append(',');
            }

            return "fm-" + Digest(builder.ToString());
        }

        public static string BuildDrawRequestId(string roundId, ulong stateVersion)
        {
            return "fm-draw-" + Digest(
                "draw|" + (roundId ?? string.Empty) + "|" +
                stateVersion.ToString(CultureInfo.InvariantCulture));
        }

        public static bool TryCreateActionPlan(
            SessionViewState session,
            PlayerTableViewState view,
            TableCommandIntentFactory factory,
            long nowMonotonicMs,
            out FullMatchActionPlan? plan,
            out string failureCode,
            out string detail)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            plan = null;
            failureCode = string.Empty;
            detail = string.Empty;

            if (view.ConnectionState != TableConnectionState.Ready ||
                session.Binding == null ||
                !view.Identity.IsPrivateAuthorized)
            {
                failureCode = "SESSION_NOT_READY";
                detail = "The confirmed session has no authorized player binding.";
                return false;
            }

            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                failureCode = "DECISION_REQUIRED";
                detail = "No server decision is currently open.";
                return false;
            }

            if (decision.Countdown.IsExpired(nowMonotonicMs))
            {
                failureCode = "DEADLINE_EXPIRED";
                detail = "The authoritative decision deadline has already expired; no action may be submitted.";
                return false;
            }

            PlayerLegalActionState? action = SelectAction(view);
            if (action == null)
            {
                failureCode = "NO_KNOWN_CANDIDATE";
                detail = "The confirmed decision has no supported legal action.";
                return false;
            }

            if (view.Own?.Control == null)
            {
                failureCode = "CONTROL_UNAVAILABLE";
                detail = "The confirmed private view has no authoritative control state.";
                return false;
            }

            if (view.Own.Control.ControlledByComputer)
            {
                failureCode = "CONTROL_AUTOMATED";
                detail = "The computer currently controls this seat; the driver cannot submit a human action.";
                return false;
            }

            string requestId = BuildSubmitRequestId(
                view.Identity.RoundId,
                decision.DecisionId,
                action.Action,
                action.OptionId,
                action.TileIds);
            var owner = new TableCommandOwnerContext(
                new RequestId(requestId),
                new RequestScopeId(view.Identity.ScopeId),
                new StateVersion(view.Identity.StateVersion),
                new OwnerEpoch(view.Identity.OwnerEpoch));

            TableCommandIntent intent;
            try
            {
                intent = factory.CreateSubmitAction(view, action, owner);
            }
            catch (TableIntentUnavailableException exception)
            {
                failureCode = exception.Code;
                detail = exception.Message;
                return false;
            }

            plan = new FullMatchActionPlan(
                intent.Command,
                intent,
                requestId,
                action.Action,
                view.Identity.RoundId,
                decision.DecisionId,
                view.Identity.Seat,
                view.Identity.SessionId,
                view.Identity.RoomId,
                session.Binding!.TableId,
                view.Identity.ScopeId,
                view.Identity.BindingEpoch,
                view.Identity.ConnectionEpoch,
                view.Identity.OwnerEpoch,
                view.Identity.StateVersion);
            return true;
        }

        /// <summary>
        /// A draw is a mandatory no-choice transition. The protocol has no
        /// server-issued candidate/decision for it, so the driver uses the accepted
        /// local-only canonical payload shape and still submits it through the real
        /// Client session. It does not invent a candidate or a win.
        /// </summary>
        public static bool TryCreateMandatoryDrawPlan(
            SessionViewState session,
            PlayerTableViewState view,
            long nowMonotonicMs,
            out FullMatchActionPlan? plan,
            out string failureCode,
            out string detail)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (view == null) throw new ArgumentNullException(nameof(view));
            plan = null;
            failureCode = string.Empty;
            detail = string.Empty;
            if (view.ConnectionState != TableConnectionState.Ready ||
                session.Binding == null ||
                !view.Identity.IsPrivateAuthorized ||
                view.Own == null)
            {
                failureCode = "SESSION_NOT_READY";
                detail = "No authorized player view is available for the mandatory draw.";
                return false;
            }

            if (view.Own.Control == null)
            {
                failureCode = "CONTROL_UNAVAILABLE";
                detail = "The confirmed private view has no authoritative control state.";
                return false;
            }

            if (view.Own.Control.ControlledByComputer)
            {
                failureCode = "CONTROL_AUTOMATED";
                detail = "The computer controls this seat; the driver must not issue a draw.";
                return false;
            }

            if (view.Board.Phase != PlayerGamePhase.Draw &&
                view.Board.Phase != PlayerGamePhase.KanDraw)
            {
                failureCode = "DRAW_NOT_AVAILABLE";
                detail = "The confirmed phase is not DRAW/KAN_DRAW.";
                return false;
            }

            if (view.Board.CurrentSeat != view.Identity.Seat)
            {
                failureCode = "DRAW_NOT_OWN_TURN";
                detail = "The mandatory draw is not for this seat.";
                return false;
            }

            string requestId = BuildDrawRequestId(view.Identity.RoundId, view.Identity.StateVersion);
            ulong controlEpoch = view.Own.Control.ControlEpoch;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                view.Identity.RoundId,
                "draw-" + view.Identity.RoundId,
                controlEpoch,
                ProtocolActionType.DrawTile,
                null,
                null);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId(view.Identity.ScopeId),
                new StateVersion(view.Identity.StateVersion),
                new RoundId(view.Identity.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(controlEpoch),
                ownerEpoch: new OwnerEpoch(view.Identity.OwnerEpoch));
            plan = new FullMatchActionPlan(
                command,
                null,
                requestId,
                PlayerActionKind.DrawTile,
                view.Identity.RoundId,
                string.Empty,
                view.Identity.Seat,
                view.Identity.SessionId,
                view.Identity.RoomId,
                session.Binding!.TableId,
                view.Identity.ScopeId,
                view.Identity.BindingEpoch,
                view.Identity.ConnectionEpoch,
                view.Identity.OwnerEpoch,
                view.Identity.StateVersion);
            return true;
        }

        /// <summary>
        /// Strict own-action event predicate. It is the unchanged
        /// <see cref="SmokeActionEventPolicy"/> semantics plus the exact accepted
        /// binding/round identity; another seat, another type or an old binding never
        /// confirms this request.
        /// </summary>
        public static bool IsOwnActionEvent(
            FullMatchActionPlan plan,
            ObservedVisibleEventBatch batch,
            ObservedVisibleEvent ev,
            ulong appliedCommandVersion)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (ev == null) throw new ArgumentNullException(nameof(ev));
            bool terminalDraw = plan.Action == PlayerActionKind.DrawTile &&
                                ev.Type == Mahjong.Contracts.ProtocolEventType.RoundEnded;
            if (!batch.Incremental ||
                batch.StateVersion <= plan.BaselineStateVersion ||
                ev.CommandVersion != appliedCommandVersion ||
                !string.Equals(batch.RoundId, plan.RoundId, StringComparison.Ordinal) ||
                !string.Equals(batch.SessionId, plan.SessionId, StringComparison.Ordinal) ||
                !string.Equals(batch.RoomId, plan.RoomId, StringComparison.Ordinal) ||
                !string.Equals(batch.TableId, plan.TableId, StringComparison.Ordinal) ||
                !string.Equals(batch.ScopeId, plan.ScopeId, StringComparison.Ordinal) ||
                (!terminalDraw && batch.Seat != plan.Seat) ||
                batch.BindingEpoch != plan.BindingEpoch ||
                batch.ConnectionEpoch != plan.ConnectionEpoch ||
                batch.OwnerEpoch != plan.OwnerEpoch)
            {
                return false;
            }

            if (terminalDraw)
            {
                // A mandatory draw at an exhausted wall has no TileDrawn event;
                // the authoritative exhaustive-draw settlement is the confirmation.
                // The settlement batch is table-owned, so it need not carry the
                // drawing seat; all identity/binding/scope checks above still apply.
                return true;
            }

            return SmokeActionEventPolicy.IsRequiredEvent(plan.Action, plan.Seat, ev);
        }

        /// <summary>
        /// Captures the round-end facts only from the confirmed client snapshot. It
        /// requires the terminal event pair and the exact round/settlement identity;
        /// a mere phase change or a score row is not enough.
        /// </summary>
        public static bool TryCaptureTerminal(
            WirePlayerSnapshot raw,
            PlayerTableViewState view,
            out FullMatchRoundTerminal? terminal,
            out string failureCode,
            out string detail)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (view == null) throw new ArgumentNullException(nameof(view));
            terminal = null;
            failureCode = string.Empty;
            detail = string.Empty;
            if (view.Board.Phase != PlayerGamePhase.RoundEnd)
            {
                failureCode = "ROUND_NOT_TERMINAL";
                detail = "The confirmed phase is not ROUND_END.";
                return false;
            }

            string roundId = raw.RoundId ?? string.Empty;
            if (roundId.Length == 0 ||
                !string.Equals(roundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                failureCode = "TERMINAL_ROUND_MISMATCH";
                detail = "The terminal snapshot round does not match the confirmed identity.";
                return false;
            }

            if (raw.PublicView == null ||
                raw.PublicView.Session == null ||
                raw.PublicView.Session.FinalScores.Count != 4)
            {
                failureCode = "TERMINAL_SCORES_MISSING";
                detail = "The terminal snapshot has no four authoritative final score rows.";
                return false;
            }

            if (!string.Equals(raw.PublicView.SettlementRoundId, roundId, StringComparison.Ordinal))
            {
                failureCode = "TERMINAL_SETTLEMENT_ROUND";
                detail = "The settlement round id does not equal the terminal round id.";
                return false;
            }

            var scores = new long[4];
            for (int i = 0; i < 4; i++)
            {
                scores[i] = raw.PublicView.Session.FinalScores[i].Score;
            }

            int sticks = (int)(raw.PublicView.Board?.RiichiSticks ?? 0U);
            var terminalTypes = new List<string>();
            bool roundEnded = false;
            string outcome = string.Empty;
            int winner = -1;
            int loser = -1;
            for (int i = 0; i < raw.PublicView.SettlementEvents.Count; i++)
            {
                Mahjong.Contracts.Wire.PublicEventEnvelope ev = raw.PublicView.SettlementEvents[i];
                if (ev == null)
                {
                    continue;
                }

                switch (ev.EventType)
                {
                    case Mahjong.Contracts.Wire.EventType.RoundEnded:
                        roundEnded = true;
                        terminalTypes.Add("RoundEnded");
                        break;
                    case Mahjong.Contracts.Wire.EventType.TsumoWin:
                        outcome = "Tsumo";
                        winner = ev.TsumoWin?.Player ?? -1;
                        terminalTypes.Add("TsumoWin");
                        break;
                    case Mahjong.Contracts.Wire.EventType.RonWin:
                        outcome = "Ron";
                        winner = ev.RonWin?.Winner ?? -1;
                        loser = ev.RonWin?.Loser ?? -1;
                        terminalTypes.Add("RonWin");
                        break;
                    case Mahjong.Contracts.Wire.EventType.DrawGame:
                        outcome = "Draw";
                        terminalTypes.Add("DrawGame");
                        break;
                    case Mahjong.Contracts.Wire.EventType.ScoreSettled:
                        terminalTypes.Add("ScoreSettled");
                        break;
                }
            }

            if (outcome.Length == 0)
            {
                failureCode = "TERMINAL_OUTCOME_MISSING";
                detail = "The settlement events contain no Tsumo, Ron or Draw outcome.";
                return false;
            }

            if (!roundEnded)
            {
                failureCode = "TERMINAL_ROUND_EVENT_MISSING";
                detail = "The settlement events contain no RoundEnded fact.";
                return false;
            }

            long total = 0;
            for (int i = 0; i < scores.Length; i++)
            {
                total += scores[i];
            }

            if (total + (sticks * 1000L) != 100_000L)
            {
                failureCode = "TERMINAL_CONSERVATION";
                detail = "Final scores plus riichi sticks do not conserve 100000 points.";
                return false;
            }

            terminal = new FullMatchRoundTerminal(
                roundId,
                scores,
                sticks,
                outcome,
                winner,
                loser,
                raw.StateVersion,
                raw.EventSeq,
                terminalTypes);
            return true;
        }

        /// <summary>
        /// Cross-checks the coordinator's durable match completion against every
        /// client-observed terminal round. The driver does not compute the final
        /// ranking; it only asserts that the authoritative match facts are complete
        /// and consistent with the confirmed rounds.
        /// </summary>
        public static bool ValidateMatchCompletion(
            LocalMatchSnapshot match,
            IReadOnlyList<FullMatchRoundTerminal> terminals,
            out string failureCode,
            out string detail)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (terminals == null) throw new ArgumentNullException(nameof(terminals));
            failureCode = string.Empty;
            detail = string.Empty;
            if (!match.Finished)
            {
                failureCode = "MATCH_NOT_FINISHED";
                detail = "The durable match coordinator has not reached its terminal state.";
                return false;
            }

            if (match.FinalScores.Count != 4 || match.RankingSeats.Count != 4)
            {
                failureCode = "MATCH_FINAL_FACTS_MISSING";
                detail = "The finished match has no four final scores/rankings.";
                return false;
            }

            long total = 0;
            for (int i = 0; i < match.FinalScores.Count; i++)
            {
                total += match.FinalScores[i];
            }

            if (total + (match.RiichiSticks * 1000L) != 100_000L)
            {
                failureCode = "MATCH_FINAL_CONSERVATION";
                detail = "The match final scores plus riichi sticks do not conserve 100000 points.";
                return false;
            }

            var seats = new HashSet<int>();
            for (int i = 0; i < match.RankingSeats.Count; i++)
            {
                int seat = match.RankingSeats[i];
                if (seat < 0 || seat > 3 || !seats.Add(seat))
                {
                    failureCode = "MATCH_RANKINGS_INVALID";
                    detail = "The final rankings do not contain four distinct seats.";
                    return false;
                }
            }

            if (match.Records.Count != terminals.Count)
            {
                failureCode = "MATCH_ROUND_COUNT";
                detail = "The match has " + match.Records.Count + " records but the client observed " +
                         terminals.Count + " terminal rounds.";
                return false;
            }

            var roundIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < terminals.Count; i++)
            {
                FullMatchRoundTerminal terminal = terminals[i];
                LocalMatchRoundRecord record = match.Records[i];
                if (!roundIds.Add(terminal.RoundId))
                {
                    failureCode = "MATCH_ROUND_DUPLICATED";
                    detail = "The client terminal round list repeats '" + terminal.RoundId + "'.";
                    return false;
                }

                if (!string.Equals(record.RoundId, terminal.RoundId, StringComparison.Ordinal) ||
                    !FullMatchRoundGuard.SameScores(record.FinalScores, terminal.FinalScores) ||
                    record.RemainingRiichiSticks != terminal.RemainingRiichiSticks)
                {
                    failureCode = "MATCH_ROUND_RECONCILIATION";
                    detail = "Round " + (i + 1) + " match record does not equal the confirmed terminal facts.";
                    return false;
                }
            }

            LocalMatchRoundRecord last = match.Records[match.Records.Count - 1];
            if (!FullMatchRoundGuard.SameScores(last.FinalScores, match.FinalScores))
            {
                failureCode = "MATCH_FINAL_SCORES";
                detail = "The last round settlement does not equal the match final scores.";
                return false;
            }

            if (string.IsNullOrEmpty(match.FinishReason))
            {
                failureCode = "MATCH_FINISH_REASON_MISSING";
                detail = "The finished match has no reason code.";
                return false;
            }

            return true;
        }

        private static string Digest(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            var builder = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
            {
                builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    internal static class FullMatchCopy
    {
        public static IReadOnlyList<long> Longs(IReadOnlyList<long> values)
        {
            if (values == null || values.Count == 0)
            {
                return Array.AsReadOnly(Array.Empty<long>());
            }

            var copy = new long[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return Array.AsReadOnly(copy);
        }

        public static IReadOnlyList<string> Strings(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return Array.AsReadOnly(Array.Empty<string>());
            }

            var copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                copy[i] = values[i] ?? string.Empty;
            }

            return Array.AsReadOnly(copy);
        }
    }
}
