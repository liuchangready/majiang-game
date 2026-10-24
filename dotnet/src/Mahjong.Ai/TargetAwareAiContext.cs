using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Raised when target-aware strategy input is missing, belongs to another decision window,
    /// or cannot be mapped to the frozen candidate list without guessing.
    /// </summary>
    public sealed class TargetAwareAiContextException : InvalidOperationException
    {
        public TargetAwareAiContextException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// One physical meld candidate's visible composition. V4's <c>GameAction.DeclareChi</c>
    /// carried a complete <c>Meld</c> (three tiles including the called tile) while
    /// <c>GameAction.DeclarePon</c> carried only the two matching tiles from the viewer's own
    /// hand; the called tile was the public last discard. The two subclasses keep those two
    /// different physical layouts explicit instead of letting a strategy infer them from a
    /// tile id or an option id.
    ///
    /// The fact is bound to a complete <see cref="LegalActionOption"/> identity
    /// (kind, seat, physical tile and option id), not merely to <c>OptionId</c>.
    /// </summary>
    public abstract class TargetAwareMeldFact
    {
        private readonly ReadOnlyCollection<TileId> _tiles;

        internal TargetAwareMeldFact(
            LegalActionOption candidate,
            IReadOnlyList<TileId> tiles,
            TileId calledTile,
            string parameterName)
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            if (tiles == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var copy = new List<TileId>(tiles.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (!seen.Add(tile.Value))
                {
                    throw new ArgumentException(
                        "A candidate meld fact cannot repeat a physical tile.",
                        parameterName);
                }

                copy.Add(tile);
            }

            _tiles = new ReadOnlyCollection<TileId>(copy);
            CalledTile = calledTile;
        }

        /// <summary>The exact legal candidate this fact describes.</summary>
        public LegalActionOption Candidate { get; }

        /// <summary>
        /// For a Chi fact this is the complete three-tile meld (including the called tile).
        /// For a Pon fact this is the two physical tiles taken from the viewer's own hand.
        /// </summary>
        public IReadOnlyList<TileId> Tiles => _tiles;

        /// <summary>The public discard that is being claimed.</summary>
        public TileId CalledTile { get; }

        internal abstract LegalActionKind RequiredKind { get; }

        internal abstract void ValidateStructure();

        /// <summary>
        /// Re-checks the fact's invariants. The constructor is the primary boundary; the
        /// context checks again so a deserialized or otherwise fabricated instance cannot
        /// bypass the kind/count checks.
        /// </summary>
        internal void Validate()
        {
            if (Candidate == null)
            {
                throw new TargetAwareAiContextException("A target-aware meld fact requires a candidate.");
            }

            if (Candidate.Kind != RequiredKind)
            {
                throw new TargetAwareAiContextException(
                    "A " + RequiredKind + " fact cannot describe a " + Candidate.Kind + " candidate.");
            }

            if (Candidate.Tile.HasValue && Candidate.Tile.Value != CalledTile)
            {
                throw new TargetAwareAiContextException(
                    "The candidate tile and the fact called tile must be the same physical tile.");
            }

            ValidateStructure();
        }
    }

    /// <summary>
    /// A Chi candidate's complete public meld. V4's <c>Meld.tiles</c> contained all three
    /// tiles, one of which is the called discard.
    /// </summary>
    public sealed class TargetAwareChiFact : TargetAwareMeldFact
    {
        public TargetAwareChiFact(
            LegalActionOption candidate,
            IReadOnlyList<TileId> meldTiles,
            TileId calledTile)
            : base(candidate, meldTiles, calledTile, nameof(meldTiles))
        {
            Validate();
        }

        internal override LegalActionKind RequiredKind => LegalActionKind.Chi;

        internal override void ValidateStructure()
        {
            if (Tiles.Count != 3)
            {
                throw new TargetAwareAiContextException("A Chi fact must contain exactly three physical tiles.");
            }

            bool calledFound = false;
            TileSuit suit = TileSuit.Man;
            bool hasSuit = false;
            int minRank = int.MaxValue;
            int maxRank = int.MinValue;
            var seenRanks = new bool[10];
            for (int i = 0; i < Tiles.Count; i++)
            {
                TileId tile = Tiles[i];
                if (tile == CalledTile)
                {
                    calledFound = true;
                }

                if (!tile.IsNumberTile)
                {
                    throw new TargetAwareAiContextException("A Chi fact can only contain number tiles.");
                }

                if (!hasSuit)
                {
                    suit = tile.Suit;
                    hasSuit = true;
                }
                else if (tile.Suit != suit)
                {
                    throw new TargetAwareAiContextException("A Chi fact must contain one suit.");
                }

                if (seenRanks[tile.Rank])
                {
                    throw new TargetAwareAiContextException("A Chi fact cannot repeat a rank.");
                }

                seenRanks[tile.Rank] = true;
                if (tile.Rank < minRank)
                {
                    minRank = tile.Rank;
                }

                if (tile.Rank > maxRank)
                {
                    maxRank = tile.Rank;
                }
            }

            if (!calledFound)
            {
                throw new TargetAwareAiContextException("A Chi fact must contain the called tile.");
            }

            // Three distinct physical tiles of one suit with rank span two are exactly a
            // consecutive 1-2-3 .. 7-8-9 sequence.
            if (maxRank - minRank != 2)
            {
                throw new TargetAwareAiContextException("A Chi fact must be a consecutive three-tile sequence.");
            }
        }
    }

    /// <summary>
    /// A Pon candidate's two physical hand tiles. V4's <c>LegalActionProvider</c> emitted
    /// <c>DeclarePon(seat, matching.take(2))</c>; the third tile is the public discard.
    /// </summary>
    public sealed class TargetAwarePonFact : TargetAwareMeldFact
    {
        public TargetAwarePonFact(
            LegalActionOption candidate,
            IReadOnlyList<TileId> handTiles,
            TileId calledTile)
            : base(candidate, handTiles, calledTile, nameof(handTiles))
        {
            Validate();
        }

        internal override LegalActionKind RequiredKind => LegalActionKind.Pon;

        internal override void ValidateStructure()
        {
            if (Tiles.Count != 2)
            {
                throw new TargetAwareAiContextException("A Pon fact must contain exactly two physical hand tiles.");
            }

            for (int i = 0; i < Tiles.Count; i++)
            {
                TileId tile = Tiles[i];
                if (tile.TypeIndex != CalledTile.TypeIndex)
                {
                    throw new TargetAwareAiContextException(
                        "Every Pon hand tile must have the same logical type as the called tile.");
                }

                if (tile == CalledTile)
                {
                    throw new TargetAwareAiContextException(
                        "The called physical tile cannot also be one of the Pon hand tiles.");
                }
            }
        }
    }

    /// <summary>
    /// The minimal, immutable, candidate-bound visible facts that the three target-aware
    /// strategies need beyond <see cref="AiObservation"/>:
    /// <list type="bullet">
    /// <item>the exact observation identity (session, decision key, control epoch, state version);</item>
    /// <item>the frozen candidate list, in list order and with full candidate identity;</item>
    /// <item>the existing <see cref="BasicAiContext"/> (own riichi, public last discard and
    /// explicit Kan/Other facts used by the BasicStrategy fallback);</item>
    /// <item>one <see cref="TargetAwareChiFact"/> for every Chi candidate and one
    /// <see cref="TargetAwarePonFact"/> for every Pon candidate.</item>
    /// </list>
    ///
    /// This type is deliberately not a copy of <see cref="AiObservation"/>: it has no wall,
    /// dead wall, opponent hand, ura-dora, seed, full tile-position map or authority callback.
    /// It cannot be used with another decision window: the strategies verify the observation
    /// identity and candidate list before choosing.
    /// </summary>
    public sealed class TargetAwareAiContext
    {
        private readonly ReadOnlyCollection<LegalActionOption> _candidates;
        private readonly ReadOnlyCollection<TargetAwareMeldFact> _meldFacts;
        private readonly BasicAiContext _basicContext;

        public TargetAwareAiContext(
            AiObservation observation,
            IReadOnlyList<TargetAwareMeldFact> meldFacts)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (meldFacts == null)
            {
                throw new ArgumentNullException(nameof(meldFacts));
            }

            _basicContext = observation.BasicContext ??
                throw new TargetAwareAiContextException(
                    "Target-aware strategies require AiObservation.BasicContext for own riichi, " +
                    "the public last discard and explicit Kan/Other fallback facts.");
            if (_basicContext.Seat != observation.Seat)
            {
                throw new TargetAwareAiContextException(
                    "Basic context must belong to the target-aware observation seat.");
            }

            // Reuse the accepted Basic contract: every Kan/Other candidate must be explicitly
            // classified and every fact must name a candidate in this exact list.
            BasicAiContext.ValidateCandidateList(_basicContext, observation.LegalActions);

            var candidates = new List<LegalActionOption>(observation.LegalActions.Count);
            for (int i = 0; i < observation.LegalActions.Count; i++)
            {
                candidates.Add(observation.LegalActions[i]);
            }

            var facts = new List<TargetAwareMeldFact>(meldFacts.Count);
            for (int i = 0; i < meldFacts.Count; i++)
            {
                TargetAwareMeldFact fact = meldFacts[i] ??
                    throw new ArgumentException("Meld facts cannot be null.", nameof(meldFacts));
                fact.Validate();
                if (fact.Candidate.Seat != observation.Seat)
                {
                    throw new TargetAwareAiContextException(
                        "Every target-aware meld fact must belong to the observation seat.");
                }

                if (!ContainsCandidate(candidates, fact.Candidate))
                {
                    throw new TargetAwareAiContextException(
                        "A target-aware meld fact names a candidate that is not part of this observation.");
                }

                for (int j = 0; j < facts.Count; j++)
                {
                    if (AiGuard.ActionsEqual(facts[j].Candidate, fact.Candidate))
                    {
                        throw new TargetAwareAiContextException(
                            "A candidate can carry at most one target-aware meld fact.");
                    }
                }

                facts.Add(fact);
            }

            // Chi/Pon cannot be scored without their complete physical composition.
            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionOption candidate = candidates[i];
                if ((candidate.Kind == LegalActionKind.Chi || candidate.Kind == LegalActionKind.Pon) &&
                    !ContainsFact(facts, candidate))
                {
                    throw new TargetAwareAiContextException(
                        "Missing target-aware meld fact for " + candidate.Kind + " candidate " +
                        (candidate.OptionId ?? "<none>") + ".");
                }
            }

            _candidates = new ReadOnlyCollection<LegalActionOption>(candidates);
            _meldFacts = new ReadOnlyCollection<TargetAwareMeldFact>(facts);

            // Validate the physical composition against the observed own hand. This rejects a
            // fact that claims a tile the viewer cannot see or a called tile that is not the
            // public last discard.
            var allClosed = TargetAwareAiView.AllClosedTiles(
                observation.MyHandTileIds,
                observation.MyDrawnTileId);
            var baseTiles = TargetAwareAiView.BaseTiles(
                allClosed,
                observation.MyMelds,
                observation.Seat);
            TargetAwareAiView.ValidateMeldFacts(
                _meldFacts,
                allClosed,
                baseTiles,
                _basicContext.LastDiscard);

            SessionId = observation.SessionId;
            DecisionKey = observation.DecisionKey;
            ControlEpoch = observation.ControlEpoch;
            StateVersion = observation.StateVersion;
            LastDiscard = _basicContext.LastDiscard;
        }

        public string SessionId { get; }

        public DecisionKey DecisionKey { get; }

        public int Seat => DecisionKey.Seat;

        public long ControlEpoch { get; }

        public long StateVersion { get; }

        /// <summary>The public last discard, or null when the table has none.</summary>
        public TileId? LastDiscard { get; }

        public BasicAiContext BasicContext => _basicContext;

        /// <summary>Defensive copy of the frozen candidate list, preserving its order.</summary>
        public IReadOnlyList<LegalActionOption> Candidates => _candidates;

        /// <summary>Defensive copy of the typed Chi/Pon facts.</summary>
        public IReadOnlyList<TargetAwareMeldFact> MeldFacts => _meldFacts;

        public bool TryGetMeldFact(LegalActionOption candidate, out TargetAwareMeldFact fact)
        {
            if (candidate != null)
            {
                for (int i = 0; i < _meldFacts.Count; i++)
                {
                    if (AiGuard.ActionsEqual(_meldFacts[i].Candidate, candidate))
                    {
                        fact = _meldFacts[i];
                        return true;
                    }
                }
            }

            fact = null!;
            return false;
        }

        /// <summary>
        /// Verifies that this context belongs to exactly this observation window and candidate
        /// list. The comparison includes the session id, DecisionKey (round, seat, decision id),
        /// control epoch, state version, candidate order/full identity and the Basic context
        /// facts. A context from another window is rejected instead of being silently reused.
        /// </summary>
        internal bool MatchesObservation(AiObservation observation)
        {
            if (observation == null)
            {
                return false;
            }

            if (!string.Equals(SessionId, observation.SessionId, StringComparison.Ordinal) ||
                !DecisionKey.Equals(observation.DecisionKey) ||
                ControlEpoch != observation.ControlEpoch ||
                StateVersion != observation.StateVersion)
            {
                return false;
            }

            if (!CandidatesEqual(_candidates, observation.LegalActions))
            {
                return false;
            }

            return BasicContextEquals(_basicContext, observation.BasicContext);
        }

        /// <summary>
        /// Verifies that a raw candidate list is the exact frozen list this context was built
        /// for. This prevents reusing fact metadata with a different list that happens to share
        /// an option id.
        /// </summary>
        internal void ValidateCandidates(IReadOnlyList<LegalActionOption> legalActions)
        {
            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            if (legalActions.Count != _candidates.Count)
            {
                throw new TargetAwareAiContextException(
                    "The candidate list does not match the target-aware context window.");
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                LegalActionOption candidate = legalActions[i] ??
                    throw new TargetAwareAiContextException("Legal candidates cannot be null.");
                if (!AiGuard.ActionsEqual(candidate, _candidates[i]))
                {
                    throw new TargetAwareAiContextException(
                        "The candidate list order or full candidate identity does not match the " +
                        "target-aware context window.");
                }
            }
        }

        public override string ToString() =>
            "TargetAwareAiContext/" + DecisionKey + "/epoch" + ControlEpoch +
            "/state" + StateVersion + "/candidates" + _candidates.Count;

        private static bool ContainsCandidate(
            IReadOnlyList<LegalActionOption> candidates,
            LegalActionOption candidate)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (AiGuard.ActionsEqual(candidates[i], candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsFact(
            IReadOnlyList<TargetAwareMeldFact> facts,
            LegalActionOption candidate)
        {
            for (int i = 0; i < facts.Count; i++)
            {
                if (AiGuard.ActionsEqual(facts[i].Candidate, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CandidatesEqual(
            IReadOnlyList<LegalActionOption> left,
            IReadOnlyList<LegalActionOption> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!AiGuard.ActionsEqual(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BasicContextEquals(BasicAiContext left, BasicAiContext? right)
        {
            if (right == null ||
                left.Seat != right.Seat ||
                left.IsRiichi != right.IsRiichi ||
                !Nullable.Equals(left.LastDiscard, right.LastDiscard) ||
                left.CandidateFacts.Count != right.CandidateFacts.Count)
            {
                return false;
            }

            for (int i = 0; i < left.CandidateFacts.Count; i++)
            {
                BasicCandidateFacts a = left.CandidateFacts[i];
                BasicCandidateFacts b = right.CandidateFacts[i];
                if (!AiGuard.ActionsEqual(a.Candidate, b.Candidate) ||
                    a.KanKind != b.KanKind ||
                    a.AbortiveDraw != b.AbortiveDraw)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Pure layout helpers shared by the context and the three strategies. They operate only on
    /// tiles that are already visible to one seat; there is no wall, opponent hand or state
    /// lookup here.
    /// </summary>
    internal static class TargetAwareAiView
    {
        internal static List<TileId> AllClosedTiles(
            IReadOnlyList<TileId> myHandTileIds,
            TileId? myDrawnTileId)
        {
            if (myHandTileIds == null)
            {
                throw new ArgumentNullException(nameof(myHandTileIds));
            }

            var allClosed = new List<TileId>(myHandTileIds.Count + (myDrawnTileId.HasValue ? 1 : 0));
            var seen = new HashSet<int>();
            for (int i = 0; i < myHandTileIds.Count; i++)
            {
                TileId tile = myHandTileIds[i];
                if (!seen.Add(tile.Value))
                {
                    throw new TargetAwareAiContextException(
                        "The visible closed hand cannot repeat a physical tile.");
                }

                allClosed.Add(tile);
            }

            if (myDrawnTileId.HasValue)
            {
                TileId drawn = myDrawnTileId.Value;
                if (!seen.Contains(drawn.Value))
                {
                    allClosed.Add(drawn);
                }
            }

            return allClosed;
        }

        internal static List<TileId> BaseTiles(
            IReadOnlyList<TileId> allClosed,
            IReadOnlyList<AiVisibleMeld> myMelds,
            int seat)
        {
            if (allClosed == null)
            {
                throw new ArgumentNullException(nameof(allClosed));
            }

            if (myMelds == null)
            {
                throw new ArgumentNullException(nameof(myMelds));
            }

            var baseTiles = new List<TileId>(allClosed.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < allClosed.Count; i++)
            {
                TileId tile = allClosed[i];
                if (!seen.Add(tile.Value))
                {
                    throw new TargetAwareAiContextException(
                        "A physical tile cannot appear twice in the visible hand.");
                }

                baseTiles.Add(tile);
            }

            for (int i = 0; i < myMelds.Count; i++)
            {
                AiVisibleMeld meld = myMelds[i] ??
                    throw new ArgumentException("Visible melds cannot be null.", nameof(myMelds));
                if (meld.Seat != seat)
                {
                    throw new TargetAwareAiContextException(
                        "The viewer's melds must belong to the viewing seat.");
                }

                for (int j = 0; j < meld.Tiles.Count; j++)
                {
                    TileId tile = meld.Tiles[j];
                    if (!seen.Add(tile.Value))
                    {
                        throw new TargetAwareAiContextException(
                            "A physical tile cannot appear in both the hand and a meld, or twice in melds.");
                    }

                    baseTiles.Add(tile);
                }
            }

            return baseTiles;
        }

        internal static void ValidateMeldFacts(
            IReadOnlyList<TargetAwareMeldFact> facts,
            IReadOnlyList<TileId> allClosed,
            IReadOnlyList<TileId> baseTiles,
            TileId? lastDiscard)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (allClosed == null)
            {
                throw new ArgumentNullException(nameof(allClosed));
            }

            if (baseTiles == null)
            {
                throw new ArgumentNullException(nameof(baseTiles));
            }

            if (facts.Count == 0)
            {
                return;
            }

            if (!lastDiscard.HasValue)
            {
                throw new TargetAwareAiContextException(
                    "Chi/Pon facts require a public last discard.");
            }

            TileId called = lastDiscard.Value;
            for (int i = 0; i < facts.Count; i++)
            {
                TargetAwareMeldFact fact = facts[i];
                if (fact.CalledTile != called)
                {
                    throw new TargetAwareAiContextException(
                        "A Chi/Pon fact called tile must equal the public last discard.");
                }

                if (Contains(baseTiles, called))
                {
                    throw new TargetAwareAiContextException(
                        "The called public discard cannot already be one of the viewer's own tiles.");
                }

                if (fact is TargetAwareChiFact)
                {
                    for (int j = 0; j < fact.Tiles.Count; j++)
                    {
                        TileId tile = fact.Tiles[j];
                        if (tile == called)
                        {
                            continue;
                        }

                        if (!Contains(allClosed, tile))
                        {
                            throw new TargetAwareAiContextException(
                                "A Chi fact claims a hand tile that is not in the visible closed hand.");
                        }
                    }
                }
                else if (fact is TargetAwarePonFact)
                {
                    for (int j = 0; j < fact.Tiles.Count; j++)
                    {
                        if (!Contains(allClosed, fact.Tiles[j]))
                        {
                            throw new TargetAwareAiContextException(
                                "A Pon fact claims a hand tile that is not in the visible closed hand.");
                        }
                    }
                }
                else
                {
                    throw new TargetAwareAiContextException(
                        "Unknown target-aware meld fact type.");
                }
            }
        }

        internal static List<TileId> RemoveFirst(IReadOnlyList<TileId> tiles, TileId tile)
        {
            var remaining = new List<TileId>(tiles);
            for (int i = 0; i < remaining.Count; i++)
            {
                if (remaining[i] == tile)
                {
                    remaining.RemoveAt(i);
                    break;
                }
            }

            return remaining;
        }

        /// <summary>
        /// V4 <c>afterChi</c>: remove every non-called tile of the complete meld from the hand
        /// once, then append the complete meld. The called tile is already public and is not in
        /// the viewer's hand.
        /// </summary>
        internal static List<TileId> AfterChi(IReadOnlyList<TileId> baseTiles, TargetAwareChiFact fact)
        {
            var remaining = new List<TileId>(baseTiles);
            for (int i = 0; i < fact.Tiles.Count; i++)
            {
                TileId tile = fact.Tiles[i];
                if (tile == fact.CalledTile)
                {
                    continue;
                }

                for (int j = 0; j < remaining.Count; j++)
                {
                    if (remaining[j] == tile)
                    {
                        remaining.RemoveAt(j);
                        break;
                    }
                }
            }

            for (int i = 0; i < fact.Tiles.Count; i++)
            {
                remaining.Add(fact.Tiles[i]);
            }

            return remaining;
        }

        /// <summary>
        /// V4 <c>afterPon</c>: remove the two participating hand tiles once, then append those
        /// two tiles and the public called tile.
        /// </summary>
        internal static List<TileId> AfterPon(IReadOnlyList<TileId> baseTiles, TargetAwarePonFact fact)
        {
            var remaining = new List<TileId>(baseTiles);
            for (int i = 0; i < fact.Tiles.Count; i++)
            {
                TileId tile = fact.Tiles[i];
                for (int j = 0; j < remaining.Count; j++)
                {
                    if (remaining[j] == tile)
                    {
                        remaining.RemoveAt(j);
                        break;
                    }
                }
            }

            for (int i = 0; i < fact.Tiles.Count; i++)
            {
                remaining.Add(fact.Tiles[i]);
            }

            remaining.Add(fact.CalledTile);
            return remaining;
        }

        internal static int CountType(IReadOnlyList<TileId> tiles, int typeIndex)
        {
            int count = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].TypeIndex == typeIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Contains(IReadOnlyList<TileId> tiles, TileId tile)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
