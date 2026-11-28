using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Ai;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// One cheat candidate prepared inside the table's serial authority queue. The AI sees
    /// only <see cref="Option"/>; the matching <see cref="Envelope"/> is host-private and is
    /// revalidated by the same serial queue before any Domain transition runs.
    /// </summary>
    public sealed class TableAiCheatPlan
    {
        internal TableAiCheatPlan(AiCheatOption option, TableCheatCommandEnvelope envelope)
        {
            Option = option ?? throw new ArgumentNullException(nameof(option));
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        }

        public AiCheatOption Option { get; }

        /// <summary>Host-only command built from the same frozen snapshot as the option.</summary>
        internal TableCheatCommandEnvelope Envelope { get; }
    }

    /// <summary>
    /// Read-only per-seat cheat window captured atomically from the serial authority queue.
    /// Contains authorized candidate metadata, own seat spend this round, and filtered
    /// visible scoring facts (own hand/melds and public open melds/discards/dora); never
    /// wall order, opponent hands, other seats' closed kan tiles, or unmarked slots.
    /// </summary>
    public sealed class TableAiCheatOpportunity
    {
        private readonly ReadOnlyCollection<TableAiCheatPlan> _plans;

        internal TableAiCheatOpportunity(
            AiCheatOpportunity view,
            IReadOnlyList<TableAiCheatPlan> plans,
            DecisionId? decisionId = null,
            long? deadlineMillis = null,
            long? offerDeadlineMillis = null,
            string? offerId = null,
            string? windowId = null,
            long recoveryGeneration = 0L,
            OwnerEpoch ownerEpoch = default,
            ScoredCheatVisibleContext? visibleContext = null,
            int spentThisRound = 0)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            if (plans == null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var copy = new TableAiCheatPlan[plans.Count];
            for (int i = 0; i < plans.Count; i++)
            {
                copy[i] = plans[i] ?? throw new ArgumentException("Cheat plans cannot contain null.", nameof(plans));
            }

            _plans = new ReadOnlyCollection<TableAiCheatPlan>(copy);
            DecisionId = decisionId;
            DeadlineMillis = deadlineMillis;
            OfferDeadlineMillis = offerDeadlineMillis;
            OfferId = offerId;
            WindowId = windowId;
            RecoveryGeneration = recoveryGeneration;
            OwnerEpoch = ownerEpoch;
            VisibleContext = visibleContext;
            SpentThisRound = spentThisRound < 0 ? 0 : spentThisRound;
        }

        public AiCheatOpportunity View { get; }

        public int Seat => View.Seat;

        public string RoundId => View.RoundId;

        public long StateVersion => View.StateVersion;

        public long ControlEpoch => View.ControlEpoch;

        public DecisionId? DecisionId { get; }

        public long? DeadlineMillis { get; }

        public long? OfferDeadlineMillis { get; }

        public string? OfferId { get; }

        public string? WindowId { get; }

        public long RecoveryGeneration { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ScoredCheatVisibleContext? VisibleContext { get; }

        public int SpentThisRound { get; }

        public IReadOnlyList<TableAiCheatPlan> Plans => _plans;

        public TableAiCheatPlan? PlanFor(string optionId)
        {
            if (optionId == null)
            {
                throw new ArgumentNullException(nameof(optionId));
            }

            for (int i = 0; i < _plans.Count; i++)
            {
                if (string.Equals(_plans[i].Option.OptionId, optionId, StringComparison.Ordinal))
                {
                    return _plans[i];
                }
            }

            return null;
        }
    }

    public enum TableAiCheatCaptureStatus : byte
    {
        Captured = 0,
        Rejected = 1,
        Failed = 2
    }

    /// <summary>Outcome of a serial-queue capture of one AI cheat window.</summary>
    public sealed class TableAiCheatCaptureResult
    {
        public TableAiCheatCaptureResult(
            TableAiCheatCaptureStatus status,
            string reasonCode,
            string detail,
            TableAiCheatOpportunity? opportunity)
        {
            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            Opportunity = opportunity;
        }

        public TableAiCheatCaptureStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public TableAiCheatOpportunity? Opportunity { get; }

        public bool IsCaptured => Status == TableAiCheatCaptureStatus.Captured && Opportunity != null;
    }

    /// <summary>One host-only cheat ledger entry (amount and reason code, no hidden tiles).</summary>
    public sealed class TableCheatLedgerEntryView
    {
        internal TableCheatLedgerEntryView(string entryId, int seat, int amount, int balanceAfter, string reason)
        {
            EntryId = entryId;
            Seat = seat;
            Amount = amount;
            BalanceAfter = balanceAfter;
            Reason = reason;
        }

        public string EntryId { get; }

        public int Seat { get; }

        public int Amount { get; }

        public int BalanceAfter { get; }

        public string Reason { get; }
    }

    /// <summary>
    /// Host-only read model used when a validation runner needs per-round cheat accounting.
    /// It never exposes wall order, opponent hands or tile identities.
    /// </summary>
    public sealed class TableCheatLedgerSnapshot
    {
        internal TableCheatLedgerSnapshot(
            IReadOnlyList<int> balances,
            IReadOnlyList<TableCheatLedgerEntryView> entries,
            IReadOnlyList<int> markGroupCounts)
        {
            Balances = balances;
            Entries = entries;
            MarkGroupCounts = markGroupCounts;
        }

        public IReadOnlyList<int> Balances { get; }

        public IReadOnlyList<TableCheatLedgerEntryView> Entries { get; }

        public IReadOnlyList<int> MarkGroupCounts { get; }
    }
}
