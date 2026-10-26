using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// D-1-01: the trusted runtime state behind one room's authoritative decision flow.
    ///
    /// The Domain round remains the sole owner of legality (it prepares and installs every
    /// transition itself), while this service owns the P3.3 OPENING seam: exactly like the
    /// accepted TableRuntime harness, a real room needs an open decision for the seat the
    /// Domain currently requires to act, otherwise a legal client command has no authoritative
    /// decision to point at. Every candidate below is derived from the Domain's own committed
    /// state (phase, current seat, open response window, the acting seat's hand tiles) 閳?never
    /// invented, never guessed, and a command outside the candidate list is still re-verified
    /// by the Domain before any commit. The decision id is deterministic over the immutable
    /// Domain round fingerprint + seat, so a redundant re-open is idempotent (the actor refuses
    /// a second open for an already-open seat) and a replayed client action can never land on
    /// a newer decision. The actor itself publishes the frozen DecisionView (decision id,
    /// control epoch, deadline remaining, legal candidate actions) from its own open pending
    /// state into every player snapshot 閳?no duplicate bookkeeping here.
    /// </summary>
    public sealed class TableRoomDecisionState
    {
        private readonly object _gate = new object();

        /// <summary>The one domain-truth snapshot the trusted projection capture supplied.</summary>
        private RoundDecisionNeed? _need;

        /// <summary>seat -> the open decision THIS service opened (or observed) for that seat.</summary>
        private readonly Dictionary<int, TableLiveDecision> _live = new Dictionary<int, TableLiveDecision>();

        /// <summary>
        /// Called only by the trusted projection-context source while the actor builds a stream
        /// frame: the only moment the service may read the exact committed Domain round as
        /// source truth.
        /// </summary>
        internal void NoteCapture(string roomId, RoundStateMachine round, ulong stateVersion, long monotonicMs)
        {
            if (round == null)
            {
                return;
            }

            lock (_gate)
            {
                if (round.IsFinished)
                {
                    _need = null;
                    return;
                }

                _need = RoundDecisionNeed.Snapshot(roomId, round, stateVersion, monotonicMs);
            }
        }

        /// <summary>
        /// Replaces/clears this service's own live-decision bookkeeping for one seat. Only
        /// entries this service itself recorded are ever touched; foreign pending keys are
        /// ignored by the drive.
        /// </summary>
        internal void SetLiveDecision(int seat, TableLiveDecision? decision)
        {
            if (seat < 0 || seat > 3)
            {
                return;
            }

            lock (_gate)
            {
                if (decision == null)
                {
                    _live.Remove(seat);
                }
                else
                {
                    _live[seat] = decision;
                }
            }
        }

        internal TableLiveDecision? GetLiveDecision(int seat)
        {
            lock (_gate)
            {
                return _live.TryGetValue(seat, out TableLiveDecision? live) ? live : null;
            }
        }

        /// <summary>
        /// One per-tick drive: for every seat the last captured Domain state still requires to
        /// act, ensure exactly one open decision with the Domain's honest candidates. Returns
        /// the number of decisions opened (0 when nothing needed opening). Called from the
        /// platform tick driver after the actor's own tick, so an expiry the actor already
        /// processed is seen before an opening attempt.
        /// </summary>
        internal async Task<int> DriveOpenDecisionsAsync(TableActor actor)
        {
            RoundDecisionNeed? need;
            lock (_gate)
            {
                need = _need;
            }

            if (need == null)
            {
                return 0;
            }

            TableTimingSnapshot timing;
            try
            {
                timing = await actor.GetTimingSnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A closed/faulted actor surfaces through the tick result itself; a bookkeeping
                // refresh is never authoritative on that path.
                return 0;
            }

            // The captured need must still describe the CURRENT round and the actor's CURRENT
            // state version; otherwise the projection has not yet observed the newer domain
            // truth and the drive must not open a decision from stale facts. Every committed
            // transition pumps a fresh capture to every subscribed stream, so the truth
            // refreshes within the same tick cadence.
            if (!string.Equals(need.RoundId, timing.RoundId, StringComparison.Ordinal) ||
                need.StateVersion != timing.StateVersion.Value)
            {
                DropUntrackedBookkeeping(timing);
                return 0;
            }

            if (timing.IsPaused)
            {
                // A paused clock is a recovery/lease state; never open against it.
                return 0;
            }

            DropUntrackedBookkeeping(timing);

            List<int> requiredSeats = CollectRequiredSeats(need);
            if (requiredSeats.Count == 0)
            {
                return 0;
            }

            TableControlSnapshot control;
            try
            {
                control = await actor.GetControlSnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return 0;
            }

            int opened = 0;
            for (int index = 0; index < requiredSeats.Count; index++)
            {
                int seat = requiredSeats[index];
                if (timing.Seats[seat].PendingKey != null)
                {
                    continue;
                }

                IReadOnlyList<LegalActionOption>? candidates = DeriveCandidates(need, seat);
                if (candidates == null || candidates.Count == 0)
                {
                    continue;
                }

                long controlEpoch = control.Seats[seat].ControlEpoch;
                string decisionId = need.BuildDecisionId(seat);
                var key = new DecisionKey(need.RoundId, seat, decisionId);
                TileId? drawnTile = need.DrawnTileId;
                TableOperationResult open = await actor
                    .OpenDecisionAsync(new PendingDecision(key, controlEpoch, candidates, drawnTile))
                    .ConfigureAwait(false);
                if (open.Status == TableOperationStatus.Configured)
                {
                    long nowMs = SystemTableClock.Instance.MonotonicMillis;
                    SetLiveDecision(seat, new TableLiveDecision(
                        decisionId,
                        controlEpoch,
                        candidates,
                        nowMs,
                        nowMs + ServiceDecisionClockConfig.BaseMillis));
                    opened++;
                }

                // Any other outcome (already open again, stale epoch, mailbox full) is retried
                // on the next tick; a double open is prevented by the actor itself.
            }

            return opened;
        }

        private static DecisionClockConfig ServiceDecisionClockConfig => RoomHost.ServiceDecisionClockConfig;

        private void DropUntrackedBookkeeping(TableTimingSnapshot timing)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (timing.Seats[seat].PendingKey == null)
                {
                    TableLiveDecision? live = GetLiveDecision(seat);
                    if (live != null)
                    {
                        SetLiveDecision(seat, null);
                    }
                }
            }
        }

        private static List<int> CollectRequiredSeats(RoundDecisionNeed need)
        {
            var seats = new List<int>(4);
            switch (need.Phase)
            {
                case RoundPhase.Draw:
                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                    // The single acting seat of the draw/discard cycle.
                    if (need.CurrentSeat >= 0 && need.CurrentSeat <= 3)
                    {
                        seats.Add(need.CurrentSeat);
                    }

                    break;
                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                    for (int i = 0; i < need.WindowEligibleSeats.Count; i++)
                    {
                        int seat = need.WindowEligibleSeats[i];
                        if (seat >= 0 && seat <= 3 && seat != need.WindowFromSeat && !need.HasResponded(seat))
                        {
                            seats.Add(seat);
                        }
                    }

                    break;
                default:
                    // KanDraw and terminal phases are driven by the Domain's own transitions (a
                    // kan's rinshan draw is part of the command itself); the orchestration
                    // never fabricates a decision the Domain does not require.
                    break;
            }

            return seats;
        }

        /// <summary>
        /// Honest candidates for one required seat from the captured Domain truth. For a draw
        /// turn the acting seat is opened with the DRAW candidate (expressed with the frozen
        /// OTHER/option id "DRAW": the candidate kind vocabulary carries no draw kind, and the
        /// Domain still verifies the actual DrawTile command before any commit); for a
        /// cheat-window/discard turn it receives one Discard candidate per hand tile (the
        /// Domain still verifies the exact submitted tile and the drawn-tile lock); for a
        /// response window the always-legal PASS is the only published candidate: a chi/pon/
        /// kan claim command remains a legal wire command and is re-validated by the Domain
        /// when it is submitted.
        /// </summary>
        private static IReadOnlyList<LegalActionOption>? DeriveCandidates(RoundDecisionNeed need, int seat)
        {
            switch (need.Phase)
            {
                case RoundPhase.Draw:
                    if (seat != need.CurrentSeat)
                    {
                        return null;
                    }

                    return new[]
                    {
                        new LegalActionOption(LegalActionKind.Other, seat, null, "DRAW")
                    };
                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                {
                    IReadOnlyList<TileId> hand = need.HandTiles(seat);
                    var candidates = new List<LegalActionOption>(hand.Count);
                    for (int i = 0; i < hand.Count; i++)
                    {
                        candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, hand[i]));
                    }

                    return candidates;
                }
                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                    return new[]
                    {
                        new LegalActionOption(LegalActionKind.Pass, seat)
                    };
                default:
                    return null;
            }
        }
    }

    /// <summary>One decision this service opened, kept for injection-time bookkeeping.</summary>
    public sealed class TableLiveDecision
    {
        internal TableLiveDecision(
            string decisionId,
            long controlEpoch,
            IReadOnlyList<LegalActionOption> candidates,
            long openedMonotonicMs,
            long deadlineMonotonicMs)
        {
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            Candidates = candidates;
            OpenedMonotonicMs = openedMonotonicMs;
            DeadlineMonotonicMs = deadlineMonotonicMs;
        }

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public IReadOnlyList<LegalActionOption> Candidates { get; }

        public long OpenedMonotonicMs { get; }

        public long DeadlineMonotonicMs { get; }
    }

    /// <summary>
    /// The immutable Domain-truth snapshot for one capture. All values are read from the
    /// committed round instance itself. The frozen decision-id derivation uses the round's own
    /// source fingerprint + version + seat, so one Domain truth produces exactly one
    /// authoritative open decision per required seat.
    /// </summary>
    public sealed class RoundDecisionNeed
    {
        private RoundDecisionNeed(
            string roundId,
            ulong stateVersion,
            ulong fingerprintSeed,
            RoundPhase phase,
            int currentSeat,
            TileId? drawnTileId,
            int windowFromSeat,
            IReadOnlyList<int> windowEligibleSeats,
            IReadOnlyDictionary<int, IReadOnlyList<TileId>> hands,
            IReadOnlyDictionary<int, bool> responded)
        {
            RoundId = roundId;
            StateVersion = stateVersion;
            FingerprintSeed = fingerprintSeed;
            Phase = phase;
            CurrentSeat = currentSeat;
            DrawnTileId = drawnTileId;
            WindowFromSeat = windowFromSeat;
            WindowEligibleSeats = windowEligibleSeats;
            Hands = hands;
            Responded = responded;
        }

        internal static RoundDecisionNeed Snapshot(
            string roomId,
            RoundStateMachine round,
            ulong stateVersion,
            long monotonicMs)
        {
            ulong seed = round.SourceFingerprint ^ (unchecked((ulong)round.Version) * 0x9E3779B97F4A7C15UL);
            var hands = new Dictionary<int, IReadOnlyList<TileId>>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                var copy = new List<TileId>(hand.Count);
                copy.AddRange(hand);
                hands[seat] = copy;
            }

            var eligible = new List<int>();
            int fromSeat = -1;
            RoundResponseWindow? window = round.Window;
            if (window != null)
            {
                fromSeat = window.FromSeat;
                foreach (int seat in window.EligibleSeats)
                {
                    if (seat >= 0 && seat <= 3)
                    {
                        eligible.Add(seat);
                    }
                }
            }

            var responded = new Dictionary<int, bool>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                responded[seat] = window != null && window.HasResponded(seat);
            }

            int currentSeat = round.CurrentSeat;
            TileId? drawnTileId =
                round.Tiles.DrawnTile.HasValue &&
                round.Tiles.DrawnTile.Value.Seat == currentSeat
                    ? round.Tiles.DrawnTile.Value.Tile
                    : null;
            return new RoundDecisionNeed(
                round.RoundId,
                stateVersion,
                seed,
                round.Phase,
                currentSeat,
                drawnTileId,
                fromSeat,
                eligible,
                hands,
                responded);
        }

        public string RoundId { get; }

        public ulong StateVersion { get; }

        public RoundPhase Phase { get; }

        public int CurrentSeat { get; }

        public TileId? DrawnTileId { get; }

        public int WindowFromSeat { get; }

        public IReadOnlyList<int> WindowEligibleSeats { get; }

        public IReadOnlyDictionary<int, IReadOnlyList<TileId>> Hands { get; }

        public IReadOnlyDictionary<int, bool> Responded { get; }

        public bool HasResponded(int seat) => Responded.TryGetValue(seat, out bool responded) && responded;

        internal IReadOnlyList<TileId> HandTiles(int seat) =>
            Hands.TryGetValue(seat, out IReadOnlyList<TileId>? hand) ? hand : Array.Empty<TileId>();

        internal string BuildDecisionId(int seat)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}-d{1:x16}-s{2}",
                RoundId,
                FingerprintSeed,
                seat);
        }

        private ulong FingerprintSeed { get; }
    }
}


