using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Rules;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Typed N-2 settlement intents accepted by the authoritative table queue. The four values
    /// map one-to-one to the frozen Domain settlement commands; they deliberately carry no final
    /// han/fu/points/payments. The actor derives or re-verifies every scoring input from the
    /// then-current <see cref="RoundStateMachine"/>.
    /// </summary>
    public enum TableSettlementAction : byte
    {
        Tsumo = 0,
        Ron = 1,
        ExhaustiveDraw = 2,
        AbortiveDraw = 3
    }

    /// <summary>
    /// Stable reason codes for the N-2 host boundary. Rejection codes from the frozen calculator
    /// are appended as <c>SETTLEMENT_&lt;CODE&gt;</c>; existing P3.x codes keep their meaning.
    /// </summary>
    public static class TableSettlementReasonCode
    {
        public const string AuthorityUnavailable = "SETTLEMENT_AUTHORITY_UNAVAILABLE";
        public const string FactsUnavailable = "SETTLEMENT_FACTS_UNAVAILABLE";
        public const string HostFactsInvalid = "SETTLEMENT_HOST_FACTS_INVALID";
        public const string IntentInvalid = "SETTLEMENT_INTENT_INVALID";
        public const string SourceRoundMismatch = "SETTLEMENT_SOURCE_ROUND_MISMATCH";
        public const string SourceVersionStale = "SETTLEMENT_SOURCE_VERSION_STALE";
        public const string SourceFingerprintMismatch = "SETTLEMENT_SOURCE_FINGERPRINT_MISMATCH";
        public const string RawEntryRejected = "SETTLEMENT_RAW_ENTRY_REJECTED";
        public const string SeatMismatch = "SETTLEMENT_SEAT_MISMATCH";

        public static string FromRejection(RoundSettlementRejectionCode code)
        {
            return "SETTLEMENT_" + code.ToString().ToUpperInvariant();
        }
    }

    /// <summary>
    /// The exact authoritative round identity a calculation or host fact set is bound to.
    /// Equality is ordinal on the round id and exact on version/fingerprint.
    /// </summary>
    public sealed class TableSettlementSourceRef
    {
        public TableSettlementSourceRef(string roundId, long version, ulong fingerprint)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("roundId cannot be blank.", nameof(roundId));
            }

            if (version < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Version must be non-negative.");
            }

            RoundId = roundId;
            Version = version;
            Fingerprint = fingerprint;
        }

        public string RoundId { get; }

        public long Version { get; }

        public ulong Fingerprint { get; }

        public bool Matches(RoundStateMachine round)
        {
            return round != null &&
                   string.Equals(round.RoundId, RoundId, StringComparison.Ordinal) &&
                   round.Version == Version &&
                   round.SourceFingerprint == Fingerprint;
        }

        public override string ToString()
        {
            return RoundId + "@" + Version + "#" + Fingerprint.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A typed settlement request. It has the same host/authority identity fields as a normal
    /// table command, but no settlement body: the body is computed by the actor from the current
    /// round. The inner <see cref="Envelope"/> carries a neutral placeholder command for the
    /// existing scope/receipt/durability plumbing; that placeholder is never executed.
    /// </summary>
    public sealed class TableSettlementRequest
    {
        public TableSettlementRequest(
            TableSettlementIntent intent,
            RequestId requestId,
            StateVersion expectedStateVersion,
            OwnerEpoch ownerEpoch,
            ControlEpoch controlEpoch,
            DecisionId? decisionId = null,
            long recoveryGeneration = 0L,
            RequestScopeId? scopeId = null,
            string? api = null,
            TablePayloadSha256? payloadSha256 = null)
        {
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));
            Envelope = new TableCommandEnvelope(
                BuildPlaceholderCommand(intent),
                requestId,
                expectedStateVersion,
                ownerEpoch,
                controlEpoch,
                decisionId,
                recoveryGeneration,
                scopeId,
                api,
                payloadSha256);
        }

        public TableSettlementIntent Intent { get; }

        internal TableCommandEnvelope Envelope { get; }

        private static RoundCommand BuildPlaceholderCommand(TableSettlementIntent intent)
        {
            RoundOutcome outcome;
            switch (intent.Action)
            {
                case TableSettlementAction.Tsumo:
                    outcome = RoundOutcome.Tsumo(intent.Seat);
                    break;
                case TableSettlementAction.Ron:
                    outcome = RoundOutcome.Ron(intent.Seat, (intent.Seat + 1) % 4);
                    break;
                case TableSettlementAction.ExhaustiveDraw:
                    outcome = RoundOutcome.ExhaustiveDraw(Array.Empty<int>());
                    break;
                case TableSettlementAction.AbortiveDraw:
                    outcome = RoundOutcome.AbortiveDraw(AbortiveReasonCode(intent.AbortiveReason!.Value));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(intent), intent.Action, "Unknown settlement action.");
            }

            var settlement = new RoundSettlement(
                "settlement-intent:" + intent.RoundId + ":" + (int)intent.Action + ":" + intent.Seat,
                outcome,
                new long[] { 0L, 0L, 0L, 0L },
                0,
                new PersistedUtcTime(DateTimeOffset.UnixEpoch),
                0L);
            switch (intent.Action)
            {
                case TableSettlementAction.Tsumo:
                    return RoundCommand.Tsumo(intent.RoundId, intent.Seat, settlement);
                case TableSettlementAction.Ron:
                    return RoundCommand.Ron(intent.RoundId, intent.Seat, settlement);
                case TableSettlementAction.ExhaustiveDraw:
                    return RoundCommand.ExhaustiveDraw(intent.RoundId, settlement);
                case TableSettlementAction.AbortiveDraw:
                    return RoundCommand.AbortiveDraw(intent.RoundId, settlement);
                default:
                    throw new ArgumentOutOfRangeException(nameof(intent), intent.Action, "Unknown settlement action.");
            }
        }

        internal static string AbortiveReasonCode(SettlementAbortiveDrawReason reason)
        {
            switch (reason)
            {
                case SettlementAbortiveDrawReason.FourWinds:
                    return "FOUR_WINDS";
                case SettlementAbortiveDrawReason.FourKans:
                    return "FOUR_KANS";
                case SettlementAbortiveDrawReason.FourRiichi:
                    return "FOUR_RIIICHI";
                case SettlementAbortiveDrawReason.NineTerminals:
                    return "NINE_TERMINALS";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown abortive reason.");
            }
        }
    }

    /// <summary>
    /// One typed settlement intent. The seat is the winner for Tsumo/Ron and the authority
    /// identity seat for the two draws. The abortive reason is an explicit host decision; the
    /// calculator still re-validates it against the exact round state.
    /// </summary>
    public sealed class TableSettlementIntent
    {
        public TableSettlementIntent(
            string roundId,
            TableSettlementAction action,
            int seat,
            SettlementAbortiveDrawReason? abortiveReason = null)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("roundId cannot be blank.", nameof(roundId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (action == TableSettlementAction.AbortiveDraw)
            {
                if (!abortiveReason.HasValue)
                {
                    throw new ArgumentException("An abortive draw intent requires a reason.", nameof(abortiveReason));
                }
            }
            else if (abortiveReason.HasValue)
            {
                throw new ArgumentException("Only an abortive draw intent may carry an abortive reason.", nameof(abortiveReason));
            }

            RoundId = roundId;
            Action = action;
            Seat = seat;
            AbortiveReason = abortiveReason;
        }

        public string RoundId { get; }

        public TableSettlementAction Action { get; }

        public int Seat { get; }

        public SettlementAbortiveDrawReason? AbortiveReason { get; }

        public RoundActionKind DomainAction
        {
            get
            {
                switch (Action)
                {
                    case TableSettlementAction.Tsumo:
                        return RoundActionKind.DeclareTsumo;
                    case TableSettlementAction.Ron:
                        return RoundActionKind.DeclareRon;
                    case TableSettlementAction.ExhaustiveDraw:
                        return RoundActionKind.DeclareExhaustiveDraw;
                    case TableSettlementAction.AbortiveDraw:
                        return RoundActionKind.DeclareAbortiveDraw;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Action), Action, "Unknown settlement action.");
                }
            }
        }

        public static bool IsSettlementAction(RoundActionKind action)
        {
            return action == RoundActionKind.DeclareTsumo ||
                   action == RoundActionKind.DeclareRon ||
                   action == RoundActionKind.DeclareExhaustiveDraw ||
                   action == RoundActionKind.DeclareAbortiveDraw;
        }
    }

    /// <summary>
    /// Host-verified facts that the current authoritative round/event projection cannot prove.
    /// Discard history, first discard, meld records, riichi/double-riichi timing, ippatsu
    /// interruption, rinshan/haitei/houtei/chankan, tenhou/chiihou and tenpai are deliberately
    /// absent: the runtime derives those itself. Supplying them here would let a caller forge
    /// trusted history.
    /// </summary>
    public sealed class TableSettlementSeatHostFacts
    {
        public TableSettlementSeatHostFacts(
            int seat,
            bool isIppatsu = false,
            bool isTemporaryFuriten = false,
            bool isRiichiFuriten = false)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            Seat = seat;
            IsIppatsu = isIppatsu;
            IsTemporaryFuriten = isTemporaryFuriten;
            IsRiichiFuriten = isRiichiFuriten;
        }

        public int Seat { get; }

        /// <summary>Trusted timing fact for the current riichi declaration; false when absent.</summary>
        public bool IsIppatsu { get; }

        public bool IsTemporaryFuriten { get; }

        public bool IsRiichiFuriten { get; }
    }

    /// <summary>
    /// A fact set returned by the composition root's host port. It is bound to one exact source
    /// round; a stale or foreign binding is rejected by the actor before any calculation.
    /// Exactly one record per seat is required, so a missing record can never be read as "false".
    /// </summary>
    public sealed class TableSettlementHostFacts
    {
        private readonly ReadOnlyCollection<TableSettlementSeatHostFacts> _seats;

        public TableSettlementHostFacts(
            TableSettlementSourceRef source,
            IEnumerable<TableSettlementSeatHostFacts> seats)
        {
            if (seats == null)
            {
                throw new ArgumentNullException(nameof(seats));
            }

            var copy = new List<TableSettlementSeatHostFacts>();
            foreach (TableSettlementSeatHostFacts seat in seats)
            {
                if (seat == null)
                {
                    throw new ArgumentException("Seat facts cannot contain null.", nameof(seats));
                }

                copy.Add(seat);
            }

            Source = source ?? throw new ArgumentNullException(nameof(source));
            _seats = new ReadOnlyCollection<TableSettlementSeatHostFacts>(copy);
        }

        public TableSettlementSourceRef Source { get; }

        public IReadOnlyList<TableSettlementSeatHostFacts> Seats => _seats;
    }

    /// <summary>
    /// The G1-testable host boundary for the few facts the frozen round model does not carry.
    /// The real P4.1/P5.1 composition root and network/storage adapters are later-stage work; the
    /// port exists so the actor path is testable without pretending those adapters exist.
    /// </summary>
    public interface ITableSettlementFactsPort
    {
        /// <summary>
        /// Returns facts bound to <paramref name="source"/>. Called on the table's serial
        /// consumer, immediately before the calculation. It must not mutate the round.
        /// </summary>
        TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent);
    }

    /// <summary>
    /// Business mailbox message for one typed settlement. It shares the normal bounded queue, so
    /// it is serialized with every other table mutation and cannot be applied out of order.
    /// </summary>
    internal sealed class SettlementMailboxMessage : TableMailboxMessage
    {
        public SettlementMailboxMessage(TableSettlementRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public TableSettlementRequest Request { get; }

        public TaskCompletionSource<TableOperationResult> Completion { get; } =
            new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class TableSettlementHostRejectedException : Exception
    {
        public TableSettlementHostRejectedException(string reasonCode, string message)
            : base(message)
        {
            ReasonCode = reasonCode;
        }

        public string ReasonCode { get; }
    }

    /// <summary>
    /// Builds the frozen calculator's fact model from the exact authoritative round plus the
    /// few host facts. Every field that the round/events can prove is derived here; the host
    /// cannot override it.
    /// </summary>
    internal static class TableSettlementFactBuilder
    {
        public static RoundSettlementFacts Build(
            RoundStateMachine round,
            TableSettlementIntent intent,
            TableSettlementHostFacts hostFacts,
            string resultId,
            long utcMillis,
            long monotonicMillis)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            if (hostFacts == null)
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.FactsUnavailable,
                    "No host settlement facts were supplied.");
            }

            ValidateSource(round, hostFacts.Source);
            Dictionary<int, TableSettlementSeatHostFacts> bySeat = ValidateSeats(hostFacts);

            var seats = new List<RoundSettlementSeatFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                TableSettlementSeatHostFacts host = bySeat[seat];
                Meld[] melds = BuildMelds(round, seat);
                IReadOnlyList<TileId> history = BuildDiscardHistory(round, seat);
                bool riichi = round.RiichiDeclared[seat];
                bool rinshan = intent.Action == TableSettlementAction.Tsumo &&
                               seat == intent.Seat &&
                               round.Tiles.DrawnTile.HasValue &&
                               round.Tiles.DrawnTile.Value.Source == DrawSource.Rinshan;
                bool haitei = intent.Action == TableSettlementAction.Tsumo &&
                              seat == intent.Seat &&
                              !rinshan &&
                              round.Tiles.WallRemaining == 0;
                bool houtei = intent.Action == TableSettlementAction.Ron &&
                              seat == intent.Seat &&
                              round.Tiles.WallRemaining == 0;
                bool chankan = intent.Action == TableSettlementAction.Ron &&
                               seat == intent.Seat &&
                               round.Window != null &&
                               round.Window.IsChankan;
                bool tenpai = intent.Action == TableSettlementAction.ExhaustiveDraw &&
                              IsTenpai(round, seat, melds);

                seats.Add(new RoundSettlementSeatFacts(
                    seat,
                    melds,
                    isRiichi: riichi,
                    isDoubleRiichi: riichi && IsDoubleRiichi(round, seat),
                    isIppatsu: host.IsIppatsu,
                    isRinshan: rinshan,
                    isHaitei: haitei,
                    isHoutei: houtei,
                    isChankan: chankan,
                    isTenhou: IsTenhou(round, intent, seat),
                    isChiihou: IsChiihou(round, intent, seat),
                    isTenpai: tenpai,
                    discardHistory: history,
                    firstDiscard: FirstDiscard(round, seat),
                    isFirstTurnUninterrupted: IsFirstTurnUninterrupted(round),
                    isTemporaryFuriten: host.IsTemporaryFuriten,
                    isRiichiFuriten: host.IsRiichiFuriten,
                    isIppatsuInterruptedByCallOrKan: IsIppatsuInterrupted(round, seat)));
            }

            return new RoundSettlementFacts(
                resultId,
                new PersistedUtcTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMillis)),
                monotonicMillis,
                seats,
                intent.AbortiveReason);
        }

        private static void ValidateSource(RoundStateMachine round, TableSettlementSourceRef source)
        {
            if (source == null)
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.HostFactsInvalid,
                    "Host settlement facts have no source binding.");
            }

            if (!string.Equals(source.RoundId, round.RoundId, StringComparison.Ordinal))
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.SourceRoundMismatch,
                    "Host facts belong to round '" + source.RoundId + "', not '" + round.RoundId + "'.");
            }

            if (source.Version != round.Version)
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.SourceVersionStale,
                    "Host facts version " + source.Version + " is stale; current version is " + round.Version + ".");
            }

            if (source.Fingerprint != round.SourceFingerprint)
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.SourceFingerprintMismatch,
                    "Host facts were bound to a different authoritative state with the same roundId/version.");
            }
        }

        private static Dictionary<int, TableSettlementSeatHostFacts> ValidateSeats(TableSettlementHostFacts hostFacts)
        {
            if (hostFacts.Seats.Count != 4)
            {
                throw new TableSettlementHostRejectedException(
                    TableSettlementReasonCode.HostFactsInvalid,
                    "Exactly four seat fact records are required; got " + hostFacts.Seats.Count + ".");
            }

            var bySeat = new Dictionary<int, TableSettlementSeatHostFacts>(4);
            for (int i = 0; i < hostFacts.Seats.Count; i++)
            {
                TableSettlementSeatHostFacts seat = hostFacts.Seats[i];
                if (bySeat.ContainsKey(seat.Seat))
                {
                    throw new TableSettlementHostRejectedException(
                        TableSettlementReasonCode.HostFactsInvalid,
                        "Seat " + seat.Seat + " is recorded more than once.");
                }

                bySeat.Add(seat.Seat, seat);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!bySeat.ContainsKey(seat))
                {
                    throw new TableSettlementHostRejectedException(
                        TableSettlementReasonCode.HostFactsInvalid,
                        "Seat " + seat + " has no host fact record.");
                }
            }

            return bySeat;
        }

        private static Meld[] BuildMelds(RoundStateMachine round, int seat)
        {
            var melds = new List<Meld>();
            for (int i = 0; i < round.Melds.Count; i++)
            {
                RoundMeld meld = round.Melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                melds.Add(new Meld(
                    ToMeldType(meld.Kind),
                    meld.Tiles,
                    meld.FromSeat,
                    meld.CalledTile));
            }

            return melds.ToArray();
        }

        private static MeldType ToMeldType(RoundMeldKind kind)
        {
            switch (kind)
            {
                case RoundMeldKind.Chi:
                    return MeldType.Chi;
                case RoundMeldKind.Pon:
                    return MeldType.Pon;
                case RoundMeldKind.OpenKan:
                    return MeldType.OpenKan;
                case RoundMeldKind.ClosedKan:
                    return MeldType.ClosedKan;
                case RoundMeldKind.AddedKan:
                    return MeldType.AddedKan;
                default:
                    throw new TableSettlementHostRejectedException(
                        TableSettlementReasonCode.HostFactsInvalid,
                        "Unknown round meld kind " + kind + ".");
            }
        }

        /// <summary>
        /// Complete own discard history, including tiles later called away. Committed discard
        /// events are authoritative and ordered; the current river and meld callers cover a
        /// checkpoint/restored projection that predates the event window. An empty list is a
        /// proved-empty history, never "not supplied".
        /// </summary>
        private static IReadOnlyList<TileId> BuildDiscardHistory(RoundStateMachine round, int seat)
        {
            var history = new List<TileId>();
            var seen = new HashSet<int>();
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.TileDiscarded && value.Seat == seat && value.Tile.HasValue)
                {
                    if (seen.Add(value.Tile.Value.Value))
                    {
                        history.Add(value.Tile.Value);
                    }
                }
            }

            IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
            for (int i = 0; i < river.Count; i++)
            {
                if (seen.Add(river[i].Value))
                {
                    history.Add(river[i]);
                }
            }

            for (int i = 0; i < round.Melds.Count; i++)
            {
                RoundMeld meld = round.Melds[i];
                if (meld.FromSeat != seat || meld.Kind == RoundMeldKind.ClosedKan)
                {
                    continue;
                }

                if (seen.Add(meld.CalledTile.Value))
                {
                    history.Add(meld.CalledTile);
                }
            }

            // Keep event order for the normal provider; when only a restored projection exists,
            // the deterministic river/meld order above is still a complete set (furiten and
            // four-winds use set membership/count, not the discarded tile's wall position).
            return history;
        }

        private static TileId? FirstDiscard(RoundStateMachine round, int seat)
        {
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.TileDiscarded && value.Seat == seat && value.Tile.HasValue)
                {
                    return value.Tile.Value;
                }
            }

            IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
            return river.Count > 0 ? (TileId?)river[0] : null;
        }

        private static bool IsFirstTurnUninterrupted(RoundStateMachine round)
        {
            if (round.Tiles.CountAt(TileZone.Meld) != 0 || round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                if (IsRealCallOrKan(round.Events[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsDoubleRiichi(RoundStateMachine round, int seat)
        {
            int riichiIndex = LastRiichiEventIndex(round, seat);
            if (riichiIndex < 0)
            {
                return false;
            }

            RoundEvent declaration = round.Events[riichiIndex];
            for (int i = 0; i < riichiIndex; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.TileDiscarded && value.Seat == seat)
                {
                    // Some restore/replay shapes emit the riichi discard immediately before
                    // RiichiDeclared. That discard is the declaration itself, not an earlier go.
                    bool isDeclarationDiscard =
                        i == riichiIndex - 1 &&
                        declaration.Tile.HasValue &&
                        value.Tile.HasValue &&
                        value.Tile.Value == declaration.Tile.Value;
                    if (!isDeclarationDiscard)
                    {
                        return false;
                    }
                }

                if (IsRealCallOrKan(value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsIppatsuInterrupted(RoundStateMachine round, int seat)
        {
            int riichiOrdinal = LastRiichiOrdinal(round, seat);
            if (riichiOrdinal < 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Ordinal <= riichiOrdinal)
                {
                    continue;
                }

                if (IsRealCallOrKan(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRealCallOrKan(RoundEvent value)
        {
            if (value.Type == RoundEventType.KanDeclared ||
                value.Type == RoundEventType.ChiDeclared ||
                value.Type == RoundEventType.PonDeclared)
            {
                return true;
            }

            return value.Type == RoundEventType.ResponseClaimed &&
                   !string.Equals(value.Detail, "PASS", StringComparison.Ordinal);
        }

        private static int LastRiichiEventIndex(RoundStateMachine round, int seat)
        {
            int index = -1;
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.RiichiDeclared && value.Seat == seat)
                {
                    index = i;
                }
            }

            return index;
        }

        private static int LastRiichiOrdinal(RoundStateMachine round, int seat)
        {
            int ordinal = -1;
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.RiichiDeclared && value.Seat == seat)
                {
                    ordinal = value.Ordinal;
                }
            }

            return ordinal;
        }

        private static bool IsTenhou(RoundStateMachine round, TableSettlementIntent intent, int seat)
        {
            if (intent.Action != TableSettlementAction.Tsumo || seat != intent.Seat || seat != round.DealerSeat)
            {
                return false;
            }

            DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
            if (!drawn.HasValue || drawn.Value.Seat != seat || drawn.Value.Source != DrawSource.DealExtra)
            {
                return false;
            }

            if (round.Tiles.CountAt(TileZone.Discard) != 0 ||
                round.Tiles.CountAt(TileZone.Meld) != 0 ||
                round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.TileDiscarded ||
                    IsRealCallOrKan(value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsChiihou(RoundStateMachine round, TableSettlementIntent intent, int seat)
        {
            if (intent.Action != TableSettlementAction.Tsumo || seat != intent.Seat || seat == round.DealerSeat)
            {
                return false;
            }

            DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
            if (!drawn.HasValue || drawn.Value.Seat != seat)
            {
                return false;
            }

            if (round.Tiles.CountAt(TileZone.Discard, seat) != 0 ||
                round.Tiles.CountAt(TileZone.Meld) != 0 ||
                round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (IsRealCallOrKan(value) ||
                    (value.Type == RoundEventType.TileDiscarded && value.Seat == seat))
                {
                    return false;
                }
            }

            return IsFirstTurnUninterrupted(round);
        }

        private static bool IsTenpai(RoundStateMachine round, int seat, Meld[] melds)
        {
            try
            {
                IReadOnlyList<TileId> closed = round.Tiles.TilesInZone(TileZone.Hand, seat);
                var hand = new PlayerHand(closed, melds, null);
                return HandDecomposer.CalculateWaits(hand.AllClosedTiles).Count > 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
