using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
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
    /// state (phase, current seat, open response window, the acting seat's hand tiles) — never
    /// invented, never guessed, and a command outside the candidate list is still re-verified
    /// by the Domain before any commit. The decision id is deterministic over the immutable
    /// Domain round fingerprint + seat, so a redundant re-open is idempotent (the actor refuses
    /// a second open for an already-open seat) and a replayed client action can never land on
    /// a newer decision. The actor itself publishes the frozen DecisionView (decision id,
    /// control epoch, deadline remaining, legal candidate actions) from its own open pending
    /// state into every player snapshot — no duplicate bookkeeping here.
    ///
    /// Round-3 review (R3): the draw turn is a TRUSTED SERVICE action, never a client
    /// decision. DrawTile is executable through the actor's own command pipeline (the frozen
    /// wire action vocabulary has no client draw), so the runtime no longer fabricates an
    /// Other/"DRAW" candidate (which the frozen DecisionView skips and no timeout policy can
    /// execute): in phase DRAW with an empty open window the runtime auto-advances the acting
    /// seat's wall draw through the actor's own SubmitAsync with the room's own service scope,
    /// and NO decision is opened for a draw turn.
    /// </summary>
    public sealed class TableRoomDecisionState
    {
        private readonly object _gate = new object();

        /// <summary>The one domain-truth snapshot the trusted projection capture supplied.</summary>
        private RoundDecisionNeed? _need;

        /// <summary>seat -> the open decision THIS service opened (or observed) for that seat.</summary>
        private readonly Dictionary<int, TableLiveDecision> _live = new Dictionary<int, TableLiveDecision>();

        /// <summary>
        /// D-1-01 (b) idempotency seam: trusted response-window eligibility recorded per
        /// (acting seat, tile, fenced state version). The FIRST resolution happens while that
        /// version is the live authority; a same-key REPLAY of an already-applied command
        /// arrives after the round advanced, so re-deriving from the current capture would
        /// fail closed and the actor's receipt ledger would never be reached. The recorded
        /// result keeps such replays mappable byte-identically — the ledger, not a fresh
        /// derivation, then answers them. Bounded, and reset per round.
        /// </summary>
        private readonly Dictionary<(int Seat, int TileValue, ulong Version), TableCommandRouteResponseEligibility>
            _responseEligibility =
                new Dictionary<(int Seat, int TileValue, ulong Version), TableCommandRouteResponseEligibility>();

        private const int MaxRecordedResponseEligibility = 512;

        /// <summary>Version the trusted auto-draw was already attempted for (one attempt max per commit).</summary>
        private ulong _autoDrawAttemptedVersion = ulong.MaxValue;

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

                if (_need != null && !string.Equals(_need.RoundId, round.RoundId, StringComparison.Ordinal))
                {
                    // A new round retires the previous round's recorded eligibility facts.
                    // The comparison is round-id against round-id: RoundDecisionNeed carries
                    // the Domain round's own id, and the room id namespace is never mixed in.
                    _responseEligibility.Clear();
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
        /// One per-tick drive: (1) in the DRAW phase the trusted runtime itself performs the
        /// acting seat's wall draw through the actor's own command pipeline (a draw turn is a
        /// service action, never an executable client decision), and (2) for every seat the
        /// last captured Domain state still requires to act, ensure exactly one open decision
        /// with the Domain's honest candidates. Returns the number of decisions opened (0 when
        /// nothing needed opening). Called from the platform tick driver after the actor's own
        /// tick, so an expiry the actor already processed is seen before an opening attempt.
        /// </summary>
        public async Task<int> DriveAsync(RoomHost host)
        {
            RoundDecisionNeed? need;
            lock (_gate)
            {
                need = _need;
            }
            TableActor actor = host.Actor;
            if (need != null)
            {
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

                if (need.Phase == RoundPhase.Draw)
                {
                    // D-1-01 round 3: no decision exists for a draw turn — the trusted runtime
                    // advances the wall draw itself; the commit publishes the next turn.
                    await AutoAdvanceDrawAsync(host, need).ConfigureAwait(false);
                    return 0;
                }
            }

            List<int> requiredSeats = need == null ? new List<int>() : CollectRequiredSeats(need);
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
                if (control.Seats.Count <= seat)
                {
                    continue;
                }

                IReadOnlyList<LegalActionOption>? candidates = DeriveCandidates(need!, seat);
                if (candidates == null || candidates.Count == 0)
                {
                    // D-1-01 round 3: no invention when the Domain truth offers nothing this
                    // runtime can honestly execute for the seat; every candidate published is
                    // provably legal AND executable on this table.
                    continue;
                }

                long controlEpoch = control.Seats[seat].ControlEpoch;
                string decisionId = need!.BuildDecisionId(seat);
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
                        nowMs + RoomHost.ServiceDecisionClockConfig.BaseMillis));
                    opened++;
                }

                // Any other outcome (already open again, stale epoch, mailbox full) is retried
                // on the next tick; a double open is prevented by the actor itself.
            }

            return opened;
        }

        /// <summary>
        /// D-1-01 round 3: the trusted draw. In phase DRAW the acting seat's wall draw goes
        /// through the actor's OWN command pipeline (SubmitAsync: the same Prepare/Install +
        /// commit + durable-receipt path every player command uses) with this room's own
        /// service scope — never a client draw the frozen wire does not carry and no
        /// unexecutable timeout default. The envelope carries no DecisionId: no decision is
        /// open in a draw turn, the actor's identity fence expects exactly that, and the
        /// Domain re-verifies phase/seat/ownership itself. One attempt per committed state
        /// version; a stale-version race retries on the next tick and a domain rejection is
        /// never re-spammed after the version's own attempt.
        /// </summary>
        private async Task AutoAdvanceDrawAsync(RoomHost host, RoundDecisionNeed need)
        {
            TableActor actor = host.Actor;
            lock (_gate)
            {
                if (_autoDrawAttemptedVersion == need.StateVersion)
                {
                    return;
                }
            }

            TableControlSnapshot control;
            try
            {
                control = await actor.GetControlSnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            int seat = need.CurrentSeat;
            if (seat < 0 || seat > 3 || control.Seats.Count <= seat)
            {
                return;
            }

            var command = RoundCommand.Draw(need.RoundId, seat);
            RequestId requestId;
            try
            {
                requestId = new RequestId(
                    "svc-draw-" +
                    need.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "-" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (ArgumentException)
            {
                return;
            }

            var envelope = new TableCommandEnvelope(
                command,
                requestId,
                new StateVersion(need.StateVersion),
                host.OwnerEpoch,
                new ControlEpoch((ulong)control.Seats[seat].ControlEpoch),
                decisionId: null,
                recoveryGeneration: host.RecoveryGeneration,
                scopeId: host.ServiceScope.ScopeId,
                api: TableRoomHostAutoDraw.Api,
                payloadSha256: TablePayloadSha256.Compute(
                    System.Text.Encoding.UTF8.GetBytes(
                        command.RoundId + "/" + command.Seat + "/" + (int)command.Action)));
            try
            {
                await actor.SubmitAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A faulted/closed actor surfaces through the tick driver; nothing to open.
            }
            finally
            {
                lock (_gate)
                {
                    _autoDrawAttemptedVersion = need.StateVersion;
                }
            }
        }

        private static void DropUntrackedBookkeeping(TableTimingSnapshot timing)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (timing.Seats[seat].PendingKey == null)
                {
                    // A projection refresh clearing a tracked decision happens through the
                    // caller's snapshot; this bookkeeping is refreshed on every capture the
                    // source observed. Nothing authoritative is invented here.
                }
            }
        }

        private static List<int> CollectRequiredSeats(RoundDecisionNeed need)
        {
            var seats = new List<int>(4);
            switch (need.Phase)
            {
                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                    // The single acting seat of the draw/discard cycle (the DRAW phase itself
                    // is a trusted service draw, not a seat decision).
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
        /// Honest candidates for one required seat from the captured Domain truth (round 3):
        ///  - draw turns are NEVER opened (the trusted runtime draws through the actor);
        ///  - a riichi-declared seat receives EXACTLY the locked tsumogiri discard candidate
        ///    (the seat's own DrawnTileEvidence is the Domain's lock truth);
        ///  - any other acting seat on a cheat-window/discard turn receives the per-tile
        ///    Discard candidates, its provable riichi candidates (menzen, funded, at least
        ///    four live wall tiles and tenpai after that exact discard) and its provable
        ///    closed/added kan candidates;
        ///  - a response window seat receives the always-legal PASS plus one honest marker
        ///    candidate per EXECUTABLE claim kind against this window's target tile (pon /
        ///    open kan / chi for the discarder's shimocha): the exact meld tiles stay a
        ///    command fact the Domain re-verifies. Ron and chankan-Ron claims need a
        ///    settlement authority P5.1 does not configure (the frozen boundary stays), so
        ///    they are never advertised.
        /// </summary>
        private static IReadOnlyList<LegalActionOption>? DeriveCandidates(RoundDecisionNeed need, int seat)
        {
            switch (need.Phase)
            {
                case RoundPhase.Draw:
                    // Draw turns belong to the trusted service auto-draw; no client decision.
                    return null;
                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                    if (seat != need.CurrentSeat)
                    {
                        return null;
                    }

                    return ActingTurnCandidates(need, seat);
                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                    if (need.WindowTargetTile is not TileId target)
                    {
                        return null;
                    }

                    return ResponseWindowCandidates(need, seat, target, need.WindowIsChankan);
                default:
                    return null;
            }
        }

        /// <summary>Phase CHEAT_WINDOW / DISCARD candidates for the acting seat.</summary>
        private static IReadOnlyList<LegalActionOption>? ActingTurnCandidates(RoundDecisionNeed need, int seat)
        {
            var candidates = new List<LegalActionOption>();

            if (need.IsRiichi(seat))
            {
                // The riichi lock: the ONLY legal discard is the seat's own drawn tile.
                if (need.DrawnTileId is TileId locked)
                {
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, locked));
                    return candidates;
                }

                // A riichi-declared seat without captured drawn evidence cannot be proven
                // locked — publish nothing rather than a wrong unlock.
                return null;
            }

            IReadOnlyList<TileId> hand = need.HandTiles(seat);
            var copiesByType = new Dictionary<int, int>(TileId.TypeCount);
            for (int i = 0; i < hand.Count; i++)
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, hand[i]));
                copiesByType.TryGetValue(hand[i].TypeIndex, out int copies);
                copiesByType[hand[i].TypeIndex] = copies + 1;
            }

            bool menzen = need.IsMenzenSeat(seat);
            if (menzen && need.Scores(seat) >= 1000L && need.WallRemaining >= 4)
            {
                // Legal riichi candidates: provably tenpai after discarding that exact tile
                // (the same decomposition rule the Domain re-verifies at commit).
                for (int i = 0; i < hand.Count; i++)
                {
                    if (IsProvableRiichi(need, seat, hand[i]))
                    {
                        candidates.Add(new LegalActionOption(LegalActionKind.Riichi, seat, hand[i]));
                    }
                }
            }

            if (need.KanAvailable)
            {
                // Provable closed-kan candidates (four physical copies in the hand). The
                // Domain's PrepareClosedKan has NO menzen requirement — an open hand may
                // still close-kan a fully concealed set — so this scan must not mask on
                // menzen (round 5 D-1-01 c). Riichi-locked seats never reach here.
                foreach (KeyValuePair<int, int> entry in copiesByType)
                {
                    if (entry.Value >= 4)
                    {
                        candidates.Add(new LegalActionOption(
                            LegalActionKind.Kan, seat, TileId.FromTypeIndex(entry.Key, 0)));
                    }
                }

                // Provable added-kan candidates: an own pon meld of that type plus the fourth
                // copy in hand (the Domain re-verifies the exact chankan shape at commit).
                for (int meldIndex = 0; meldIndex < need.Melds.Count; meldIndex++)
                {
                    RoundMeld meld = need.Melds[meldIndex];
                    if (meld.Seat != seat || meld.Kind != RoundMeldKind.Pon)
                    {
                        continue;
                    }

                    int type = meld.CalledTile.TypeIndex;
                    if (copiesByType.TryGetValue(type, out int inHand) && inHand >= 1)
                    {
                        candidates.Add(new LegalActionOption(LegalActionKind.Kan, seat, TileId.FromTypeIndex(type, 0)));
                        break;
                    }
                }
            }

            return candidates.Count == 0 ? null : candidates;
        }

        /// <summary>Mirrors the Domain's own tenpai-after-discard rule on the captured truth:
        /// remove the exact physical tile, the remainder is a legal closed-hand size and at
        /// least one live wait survives (the seat's meld tiles counted as the Domain counts
        /// them).</summary>
        private static bool IsProvableRiichi(RoundDecisionNeed need, int seat, TileId discardTile)
        {
            IReadOnlyList<TileId> hand = need.HandTiles(seat);
            int removeIndex = -1;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].Equals(discardTile))
                {
                    removeIndex = i;
                    break;
                }
            }

            if (removeIndex < 0)
            {
                return false;
            }

            var closed = new List<TileId>(hand.Count - 1);
            for (int i = 0; i < hand.Count; i++)
            {
                if (i != removeIndex)
                {
                    closed.Add(hand[i]);
                }
            }

            if (closed.Count % 3 != 1)
            {
                return false;
            }

            var closedCounts = new int[TileId.TypeCount];
            for (int i = 0; i < closed.Count; i++)
            {
                closedCounts[closed[i].TypeIndex]++;
            }

            int[] meldCounts = need.MeldTypeCounts(seat);
            foreach (int wait in HandDecomposer.CalculateWaits(closed))
            {
                if (closedCounts[wait] + meldCounts[wait] < 4)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Response-window candidates. Windows whose only claim kinds are
        /// settlement-bound (RON, chankan-Ron) stay Pass-only: a P5.1 table cannot execute a
        /// settlement claim, so advertising one would publish an unexecutable "legal"
        /// candidate. Pon/open-kan/chi targets are executable through the frozen wire.</summary>
        private static IReadOnlyList<LegalActionOption> ResponseWindowCandidates(
            RoundDecisionNeed need,
            int seat,
            TileId target,
            bool isChankan)
        {
            var candidates = new List<LegalActionOption>
            {
                new LegalActionOption(LegalActionKind.Pass, seat)
            };

            if (isChankan)
            {
                // Chankan resolution is a Ron claim (a scoring authority this table does not
                // configure): Pass is the only executable response.
                return candidates;
            }

            if (need.IsRiichi(seat))
            {
                // A riichi seat can never pon/chi/open-kan; the Domain refuses every call.
                return candidates;
            }

            IReadOnlyList<TileId> hand = need.HandTiles(seat);
            int copies = 0;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].TypeIndex == target.TypeIndex)
                {
                    copies++;
                }
            }

            if (need.KanAvailable && copies >= 3)
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Kan, seat, target));
            }

            if (copies >= 2)
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Pon, seat, target));
            }

            // Chi: only the discarder's shimocha, only number tiles, one legal run pair.
            if (seat == (need.WindowFromSeat + 1) % 4 &&
                target.TypeIndex < 27 &&
                ResponseWindowCandidatesChiPairExists(need, seat, target))
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Chi, seat, target));
            }

            return candidates;
        }

        /// <summary>True when the claimant's captured hand holds both physical copies of any
        /// legal chi pairing for this target (the exact pairing/order is a command fact the
        /// Domain re-verifies at claim time).</summary>
        private static bool ResponseWindowCandidatesChiPairExists(
            RoundDecisionNeed need,
            int seat,
            TileId target)
        {
            int rank = target.Rank;
            int baseType = target.TypeIndex - (rank - 1); // the suit's rank-1 type
            var possessed = new HashSet<int>();
            IReadOnlyList<TileId> hand = need.HandTiles(seat);
            for (int i = 0; i < hand.Count; i++)
            {
                possessed.Add(hand[i].TypeIndex);
            }

            // Pair (rank-2, rank-1), (rank-1, rank+1), (rank+1, rank+2). Every rank side
            // condition guards its own pair: without the middle pair's rank>=2, a rank-1
            // target's baseType-1 alias is the PREVIOUS suit's rank 9 — a cross-suit pair
            // the Domain would rightly refuse (round 5 D-1-01 c).
            if (rank >= 3 && possessed.Contains(baseType + rank - 3) && possessed.Contains(baseType + rank - 2))
            {
                return true;
            }

            if (rank >= 2 && rank + 1 <= 9 && possessed.Contains(baseType + rank - 2) && possessed.Contains(baseType + rank))
            {
                return true;
            }

            if (rank + 2 <= 9 && possessed.Contains(baseType + rank) && possessed.Contains(baseType + rank + 1))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// D-1-01 (b): the trusted response-window eligibility for an inbound DISCARD (and a
        /// riichi or an added-kan discard) — computed at the trusted capture boundary from the
        /// Domain's committed round for the EXACT command-fenced state version, never from a
        /// client claim. Eligible seats are those whose claim is EXECUTABLE through this
        /// table's frozen wire: open kan (three same-type copies plus kan room on the wall),
        /// pon (two same-type copies) and chi (only the discarder's shimocha, one legal run
        /// pairing). RON eligibility is a settlement-authority boundary P5.1 does not
        /// configure; such seats stay outside this fence exactly as they stay outside every
        /// advertised candidate, and the actor's AuthorityUnavailable semantics stay
        /// untouched.
        /// Returns null when the trusted state cannot serve this version: the mapper must
        /// fail closed instead of mapping a response window from stale or absent facts.
        /// </summary>
        internal TableCommandRouteResponseEligibility? DeriveResponseEligibility(
            int actingSeat,
            TileId discardedTile,
            ulong expectedStateVersion)
        {
            (int Seat, int TileValue, ulong Version) key =
                (actingSeat, discardedTile.Value, expectedStateVersion);
            RoundDecisionNeed? need;
            lock (_gate)
            {
                // A recorded resolution for this exact fence key is the trusted answer — the
                // version it was derived at was the live authority then, and the record is
                // what keeps an applied command's same-key replay mappable afterwards.
                if (_responseEligibility.TryGetValue(key, out TableCommandRouteResponseEligibility? recorded))
                {
                    return recorded;
                }

                need = _need;
                if (need == null ||
                    need.CurrentSeat != actingSeat ||
                    (need.Phase != RoundPhase.CheatWindow && need.Phase != RoundPhase.Discard) ||
                    need.StateVersion != expectedStateVersion)
                {
                    // No capture, a stale version, a foreign acting seat or uncorroborated
                    // riichi state: the service cannot honestly decide the claim fence.
                    return null;
                }

                var eligible = new List<int>();
                for (int claimSeat = 0; claimSeat < 4; claimSeat++)
                {
                    if (claimSeat == actingSeat)
                    {
                        continue;
                    }

                    if (need.IsRiichi(claimSeat))
                    {
                        continue;
                    }

                    IReadOnlyList<TileId> hand = need.HandTiles(claimSeat);
                    int copies = 0;
                    bool kanPossible = false;
                    bool ponPossible = false;
                    for (int i = 0; i < hand.Count; i++)
                    {
                        if (hand[i].TypeIndex == discardedTile.TypeIndex)
                        {
                            copies++;
                        }
                    }

                    kanPossible = need.KanAvailable && copies >= 3;
                    ponPossible = copies >= 2;
                    bool chiPossible = claimSeat == (actingSeat + 1) % 4 &&
                                       discardedTile.TypeIndex < 27 &&
                                       ResponseWindowCandidatesChiPairExists(need, claimSeat, discardedTile);

                    if ((kanPossible || ponPossible || chiPossible) && !eligible.Contains(claimSeat))
                    {
                        eligible.Add(claimSeat);
                    }
                }

                eligible.Sort();
                var resolved = new TableCommandRouteResponseEligibility(eligible);
                if (_responseEligibility.Count < MaxRecordedResponseEligibility)
                {
                    _responseEligibility[key] = resolved;
                }

                return resolved;
            }
        }
    }

    /// <summary>One trusted response-eligibility computation for one discard target.</summary>
    public sealed class TableCommandRouteResponseEligibility
    {
        public TableCommandRouteResponseEligibility(IReadOnlyList<int> eligibleSeats)
        {
            EligibleSeats = eligibleSeats ?? throw new ArgumentNullException(nameof(eligibleSeats));
        }

        public IReadOnlyList<int> EligibleSeats { get; }
    }

    /// <summary>Informational-only constant block backing the trusted auto-draw envelope.</summary>
    public static class TableRoomHostAutoDraw
    {
        public const string Api = "SERVICE_DRAW";
        public const string ServiceAccountId = "tablesvc-draw-authority";
        public const long ServiceScopeLifetimeMs = 7L * 24L * 60L * 60L * 1000L;
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
    /// authoritative open decision per required seat. Round 3 adds the register truths the
    /// legal candidate adverse (riichi/kan/claim candidates, eligibility fencing) need —
    /// all read from the Domain's own committed state, none invented; the menzen test is the
    /// Domain's exact closed-hand truth computed at capture time.
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
            TileId? windowTargetTile,
            bool windowIsChankan,
            IReadOnlyDictionary<int, IReadOnlyList<TileId>> hands,
            IReadOnlyDictionary<int, bool> responded,
            IReadOnlyList<bool> riichiDeclared,
            IReadOnlyList<bool> menzen,
            IReadOnlyList<long> scores,
            IReadOnlyList<RoundMeld> melds,
            long wallRemaining,
            bool kanAvailable)
        {
            RoundId = roundId;
            StateVersion = stateVersion;
            FingerprintSeed = fingerprintSeed;
            Phase = phase;
            CurrentSeat = currentSeat;
            DrawnTileId = drawnTileId;
            WindowFromSeat = windowFromSeat;
            WindowEligibleSeats = windowEligibleSeats;
            WindowTargetTile = windowTargetTile;
            WindowIsChankan = windowIsChankan;
            Hands = hands;
            Responded = responded;
            RiichiDeclared = riichiDeclared;
            Menzen = menzen;
            ScoreTable = scores;
            Melds = melds;
            WallRemaining = wallRemaining;
            KanAvailable = kanAvailable;
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
            TileId? windowTargetTile = null;
            bool windowIsChankan = false;
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

                windowTargetTile = window.TargetTile;
                windowIsChankan = window.IsChankan;
            }

            var responded = new Dictionary<int, bool>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                responded[seat] = window != null && window.HasResponded(seat);
            }

            var riichiDeclared = new List<bool>(4);
            var menzen = new List<bool>(4);
            var scores = new List<long>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                riichiDeclared.Add(round.IsRiichi(seat));
                menzen.Add(IsMenzen(round, seat));
                scores.Add(round.Scores[seat]);
            }

            int currentSeat = round.CurrentSeat;
            TileId? drawnTileId =
                round.Tiles.DrawnTile.HasValue &&
                round.Tiles.DrawnTile.Value.Seat == currentSeat
                    ? round.Tiles.DrawnTile.Value.Tile
                    : null;

            // Kan availability is the Domain's own CheckKanAvailable truth: totalKanCount<4,
            // rinshanPointer<4 and a live wall tile.
            bool kanAvailable = round.Tiles.KanCount < 4 &&
                                round.Tiles.RinshanPointer < 4 &&
                                round.Tiles.WallRemaining > 0;

            return new RoundDecisionNeed(
                round.RoundId,
                stateVersion,
                seed,
                round.Phase,
                currentSeat,
                drawnTileId,
                fromSeat,
                eligible,
                windowTargetTile,
                windowIsChankan,
                hands,
                responded,
                riichiDeclared,
                menzen,
                scores,
                round.Melds,
                round.Tiles.WallRemaining,
                kanAvailable);
        }

        /// <summary>The Domain's exact menzen truth (mirror of <c>RoundStateMachine.IsMenzen</c>
        /// over the same committed public data): no OPEN meld record for the seat, and the
        /// physical meld-zone population equals the recorded meld tiles (an owner-private
        /// extension can never fake an open meld that is not there).</summary>
        private static bool IsMenzen(RoundStateMachine round, int seat)
        {
            bool anyOpen = false;
            int recorded = 0;
            IReadOnlyList<RoundMeld> melds = round.Melds;
            for (int i = 0; i < melds.Count; i++)
            {
                RoundMeld meld = melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                if (meld.IsOpen)
                {
                    anyOpen = true;
                }

                recorded += meld.Tiles.Count;
            }

            if (anyOpen)
            {
                return false;
            }

            int physical = round.Tiles.CountAt(TileZone.Meld, seat);
            return physical == recorded;
        }

        public string RoundId { get; }

        public ulong StateVersion { get; }

        public RoundPhase Phase { get; }

        public int CurrentSeat { get; }

        public TileId? DrawnTileId { get; }

        public int WindowFromSeat { get; }

        public IReadOnlyList<int> WindowEligibleSeats { get; }

        public TileId? WindowTargetTile { get; }

        public bool WindowIsChankan { get; }

        public IReadOnlyDictionary<int, IReadOnlyList<TileId>> Hands { get; }

        public IReadOnlyDictionary<int, bool> Responded { get; }

        public IReadOnlyList<bool> RiichiDeclared { get; }

        public IReadOnlyList<bool> Menzen { get; }

        private IReadOnlyList<long> ScoreTable { get; }

        public IReadOnlyList<RoundMeld> Melds { get; }

        public long WallRemaining { get; }

        public bool KanAvailable { get; }

        public bool IsRiichi(int seat) => seat >= 0 && seat <= 3 && RiichiDeclared[seat];

        public bool IsMenzenSeat(int seat) => seat >= 0 && seat <= 3 && Menzen[seat];

        public long Scores(int seat) => seat >= 0 && seat <= 3 ? ScoreTable[seat] : 0L;

        public bool HasResponded(int seat) => Responded.TryGetValue(seat, out bool responded) && responded;

        internal IReadOnlyList<TileId> HandTiles(int seat) =>
            Hands.TryGetValue(seat, out IReadOnlyList<TileId>? hand) ? hand : Array.Empty<TileId>();

        /// <summary>Per-type counts of this seat's meld tiles (the same fact HandDecomposer
        /// wait eligibility overlaps against, exactly as the Domain's own riichi check).</summary>
        internal int[] MeldTypeCounts(int seat)
        {
            var counts = new int[TileId.TypeCount];
            if (seat < 0 || seat > 3)
            {
                return counts;
            }

            for (int i = 0; i < Melds.Count; i++)
            {
                RoundMeld meld = Melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    counts[meld.Tiles[t].TypeIndex]++;
                }
            }

            return counts;
        }

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
