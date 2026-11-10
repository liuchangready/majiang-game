using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Ai;

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
    /// Read-only per-seat cheat window captured from the authority queue. It contains only
    /// authorized candidate metadata and the seat's own point balance; no GameState, wall
    /// order, opponent hand or callback is exposed.
    /// </summary>
    public sealed class TableAiCheatOpportunity
    {
        private readonly ReadOnlyCollection<TableAiCheatPlan> _plans;

        internal TableAiCheatOpportunity(AiCheatOpportunity view, IReadOnlyList<TableAiCheatPlan> plans)
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
        }

        public AiCheatOpportunity View { get; }

        public int Seat => View.Seat;

        public string RoundId => View.RoundId;

        public long StateVersion => View.StateVersion;

        public long ControlEpoch => View.ControlEpoch;

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
