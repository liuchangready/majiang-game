using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Fixed, opt-in Player cheat scenarios for C04-G3. The enum is the only selector a
    /// caller can supply; the class never accepts a caller-built round, tile map, cheat
    /// state, ledger, receipt or event. Every scenario is a genuinely dealt 136-entity
    /// round created through the accepted public Domain factory.
    /// </summary>
    public enum LocalPlayerCheatScenarioKind
    {
        /// <summary>
        /// One fresh dealer round with 120 cheat points. The Player performs TileMark,
        /// SleeveBloom (SECOND) and BeamSwap (SWAP) on two ordinary turns.
        /// </summary>
        AuthorizedThreeCheats = 1,

        /// <summary>
        /// One fresh dealer round with 120 cheat points. The Player legally spends the
        /// balance down, then attempts one more paid trick so the authority rejects it
        /// for insufficient funds. The same round also carries the unauthorized-binding
        /// negative through the real client channel.
        /// </summary>
        InsufficientBalance = 2
    }

    /// <summary>
    /// Read-only scenario facts. These describe the fixed initial deal and the accepted
    /// local cheat economy only; they never contain a receipt, result, event, ledger entry,
    /// predicted choice or post-action snapshot.
    /// </summary>
    public sealed class LocalPlayerCheatScenarioFacts
    {
        internal LocalPlayerCheatScenarioFacts(
            string scenarioId,
            LocalPlayerCheatScenarioKind kind,
            string roomId,
            string roundId,
            int dealerSeat,
            IReadOnlyList<uint> entityIds,
            IReadOnlyList<uint> dealerHandEntityIds,
            uint drawnTileId,
            IReadOnlyList<uint> liveWallHeadEntityIds,
            IReadOnlyList<uint> proposedTileMarkTileIds,
            int initialCheatPoints,
            int tileMarkCost,
            int sleeveBloomCost,
            int beamSwapCost)
        {
            ScenarioId = scenarioId;
            Kind = kind;
            RoomId = roomId;
            RoundId = roundId;
            DealerSeat = dealerSeat;
            EntityIds = entityIds;
            DealerHandEntityIds = dealerHandEntityIds;
            DrawnTileId = drawnTileId;
            LiveWallHeadEntityIds = liveWallHeadEntityIds;
            ProposedTileMarkTileIds = proposedTileMarkTileIds;
            InitialCheatPoints = initialCheatPoints;
            TileMarkCost = tileMarkCost;
            SleeveBloomCost = sleeveBloomCost;
            BeamSwapCost = beamSwapCost;
        }

        public string ScenarioId { get; }
        public LocalPlayerCheatScenarioKind Kind { get; }
        public string RoomId { get; }
        public string RoundId { get; }
        public int DealerSeat { get; }
        public IReadOnlyList<uint> EntityIds { get; }
        public IReadOnlyList<uint> DealerHandEntityIds { get; }
        public uint DrawnTileId { get; }
        public IReadOnlyList<uint> LiveWallHeadEntityIds { get; }
        public IReadOnlyList<uint> ProposedTileMarkTileIds { get; }
        public int InitialCheatPoints { get; }
        public int TileMarkCost { get; }
        public int SleeveBloomCost { get; }
        public int BeamSwapCost { get; }
    }

    /// <summary>
    /// Builds the fixed C04-G3 controlled initial state. This type lives in the LocalHost
    /// assembly only so it can assign the existing internal <see
    /// cref="LocalTableHostOptions.InitialRound"/> and <see
    /// cref="LocalTableHostOptions.AutoPlay"/> seams. It does not add a product entry point,
    /// expose an arbitrary tile/round setter, or write any receipt/event/ledger result.
    /// </summary>
    public static class LocalPlayerCheatScenario
    {
        public const string ScenarioVersion = "c04-g3-player-cheats-v1";
        public const string DefaultRoomId = "c04-g3-cheats";
        public const string InitialRoundId = "c04-g3-r1";
        public const int DealerSeat = 0;
        public const int InitialCheatPoints = 120;
        public const int TileMarkCost = 1;
        public const int SleeveBloomCost = 10;
        public const int BeamSwapCost = 10;

        // The dealer's 14 physical entities: 1m,4m,7m,2p,5p,8p,3s,6s,9s,E,S,W,N plus the
        // drawn 1m copy. The duplicate is the drawn tile, so SleeveBloom has a real drawn
        // tile and both its candidates are physical entities already in the 136 set.
        private static readonly int[] DealerHand =
        {
            0, 12, 24, 37, 49, 61, 74, 86, 98, 108, 112, 116, 120, 1
        };

        // Fixed non-dealer hands. They are deliberately unlike the dealer hand and unlike
        // each other; no hand is a complete 4-meld-plus-pair or seven-pairs shape.
        private static readonly int[] SeatOneHand = { 4, 16, 28, 40, 52, 64, 72, 84, 96, 124, 128, 132, 5 };
        private static readonly int[] SeatTwoHand = { 8, 20, 32, 44, 56, 76, 88, 100, 109, 113, 117, 121, 2 };
        private static readonly int[] SeatThreeHand = { 9, 21, 33, 45, 57, 80, 92, 104, 110, 114, 118, 122, 3 };

        // Dealer wall slots in deal order (see TilePositionMap.BuildDealtPositions).
        private static readonly int[] DealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
        private static readonly int[] SeatOneSlots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
        private static readonly int[] SeatTwoSlots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
        private static readonly int[] SeatThreeSlots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };

        static LocalPlayerCheatScenario()
        {
            var all = new uint[TileId.Count];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = unchecked((uint)i);
            }

            EntityIds = Array.AsReadOnly(all);

            var dealt = BuildOrder();
            var wallHead = new uint[8];
            for (int i = 0; i < wallHead.Length; i++)
            {
                wallHead[i] = unchecked((uint)dealt[53 + i]);
            }

            LiveWallHeadEntityIds = Array.AsReadOnly(wallHead);
            var hand = new uint[DealerHand.Length];
            for (int i = 0; i < DealerHand.Length; i++)
            {
                hand[i] = unchecked((uint)DealerHand[i]);
            }

            DealerHandEntityIds = Array.AsReadOnly(hand);
            DrawnTileId = unchecked((uint)DealerHand[DealerHand.Length - 1]);
            var mark = new uint[4];
            for (int i = 0; i < mark.Length; i++)
            {
                mark[i] = unchecked((uint)DealerHand[i]);
            }

            ProposedTileMarkTileIds = Array.AsReadOnly(mark);
        }

        public static IReadOnlyList<uint> EntityIds { get; }

        public static IReadOnlyList<uint> DealerHandEntityIds { get; }

        public static uint DrawnTileId { get; }

        /// <summary>First eight live-wall entities in physical wall-slot order (53..60).</summary>
        public static IReadOnlyList<uint> LiveWallHeadEntityIds { get; }

        /// <summary>The four dealer hand entities the authorized scenario proposes to mark.</summary>
        public static IReadOnlyList<uint> ProposedTileMarkTileIds { get; }

        /// <summary>
        /// Creates the real local host options for one frozen scenario. Only the enum selects
        /// behaviour; <paramref name="saveDirectory"/> is an absolute isolated directory owned
        /// by the caller and <paramref name="roomId"/> must be a safe local room id.
        /// </summary>
        public static LocalTableHostOptions CreateOptions(
            string saveDirectory,
            string roomId,
            LocalPlayerCheatScenarioKind kind,
            string? ownerInstanceId = null)
        {
            if (!Enum.IsDefined(typeof(LocalPlayerCheatScenarioKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown C04 cheat scenario.");
            }

            var options = new LocalTableHostOptions
            {
                SaveDirectory = saveDirectory ?? throw new ArgumentNullException(nameof(saveDirectory)),
                RoomId = string.IsNullOrWhiteSpace(roomId) ? DefaultRoomId : roomId!,
                RulesetVersion = 1,
                OwnerInstanceId = string.IsNullOrWhiteSpace(ownerInstanceId)
                    ? "c04-player-cheats"
                    : ownerInstanceId!,
                LeaseDuration = TimeSpan.FromMinutes(10),
                AutoFlushInterval = TimeSpan.FromMilliseconds(100),
                Environment = CreateEnvironment(),
                InitialRound = CreateRound()
            };

            // The accepted local driver still advances the three Computer seats and opens the
            // human decision. It never fabricates a cheat or writes a receipt for seat 0.
            options.AutoPlay = true;
            options.Validate();
            return options;
        }

        public static LocalPlayerCheatScenarioFacts GetFacts(LocalPlayerCheatScenarioKind kind)
        {
            if (!Enum.IsDefined(typeof(LocalPlayerCheatScenarioKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown C04 cheat scenario.");
            }

            return new LocalPlayerCheatScenarioFacts(
                ScenarioVersion + "/" + ((int)kind).ToString(CultureInfo.InvariantCulture),
                kind,
                DefaultRoomId,
                InitialRoundId,
                DealerSeat,
                EntityIds,
                DealerHandEntityIds,
                DrawnTileId,
                LiveWallHeadEntityIds,
                ProposedTileMarkTileIds,
                InitialCheatPoints,
                TileMarkCost,
                SleeveBloomCost,
                BeamSwapCost);
        }

        private static RoundStateMachine CreateRound()
        {
            int[] order = BuildOrder();
            var tiles = new TileId[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                tiles[i] = new TileId(order[i]);
            }

            RecordedRandomDraw wall = RecordedRandomDraw.Create("c04-g3-fixed-wall", tiles);
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, DealerSeat);
            var spec = new RoundSpec(
                InitialRoundId,
                DealerSeat,
                1,
                1,
                0,
                new long[] { 25_000L, 25_000L, 25_000L, 25_000L },
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        /// <summary>
        /// Deterministic public pre-sequence: dealer discards the safe north honour, the three
        /// Computer seats draw/discard/pass, and the dealer draws again. The returned round is
        /// on a real normal draw (not deal-extra) with no cheap-window id, so immediate
        /// SleeveBloom is legal. No cheat command, ledger entry, receipt or projected result is
        /// written here.
        /// </summary>
        private static RoundStateMachine AdvanceToDealerSecondDraw(RoundStateMachine start)
        {
            RoundStateMachine round = start;
            bool dealerDiscarded = false;
            for (int step = 0; step < 64; step++)
            {
                if (round.Phase == RoundPhase.CheatWindow &&
                    round.CurrentSeat == DealerSeat &&
                    dealerDiscarded &&
                    round.Tiles.DrawnTile.HasValue &&
                    round.Tiles.DrawnTile.Value.Seat == DealerSeat)
                {
                    return round;
                }

                switch (round.Phase)
                {
                    case RoundPhase.CheatWindow:
                    case RoundPhase.Discard:
                    {
                        int seat = round.CurrentSeat;
                        TileId tile = round.Tiles.DrawnTile.HasValue &&
                                      round.Tiles.DrawnTile.Value.Seat == seat
                            ? round.Tiles.DrawnTile.Value.Tile
                            : round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                        if (seat == DealerSeat)
                        {
                            tile = new TileId(120);
                        }

                        RoundTransition transition = round.Execute(
                            RoundCommand.Discard(round.RoundId, seat, tile));
                        if (!transition.IsAccepted)
                        {
                            throw new InvalidOperationException(
                                "C04 pre-sequence discard failed: " + transition.Error);
                        }

                        round = transition.State!;
                        if (seat == DealerSeat)
                        {
                            dealerDiscarded = true;
                        }

                        break;
                    }

                    case RoundPhase.Draw:
                    case RoundPhase.KanDraw:
                    {
                        RoundTransition transition = round.Execute(
                            RoundCommand.Draw(round.RoundId, round.CurrentSeat));
                        if (!transition.IsAccepted)
                        {
                            throw new InvalidOperationException(
                                "C04 pre-sequence draw failed: " + transition.Error);
                        }

                        round = transition.State!;
                        break;
                    }

                    case RoundPhase.ResponseWindow:
                    case RoundPhase.ChankanWindow:
                    {
                        RoundResponseWindow? window = round.Window;
                        if (window == null)
                        {
                            throw new InvalidOperationException("C04 pre-sequence has no response window.");
                        }

                        bool passed = false;
                        for (int i = 0; i < window.EligibleSeats.Count; i++)
                        {
                            int seat = window.EligibleSeats[i];
                            if (window.HasResponded(seat))
                            {
                                continue;
                            }

                            RoundTransition transition = round.Execute(
                                RoundCommand.Pass(round.RoundId, seat));
                            if (!transition.IsAccepted)
                            {
                                throw new InvalidOperationException(
                                    "C04 pre-sequence pass failed: " + transition.Error);
                            }

                            round = transition.State!;
                            passed = true;
                            break;
                        }

                        if (!passed)
                        {
                            throw new InvalidOperationException("C04 pre-sequence response window did not resolve.");
                        }

                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            "C04 pre-sequence cannot continue from phase " + round.Phase + ".");
                }
            }

            throw new InvalidOperationException("C04 pre-sequence did not reach the dealer's second draw.");
        }

        private static int[] BuildOrder()
        {
            var order = new int[TileId.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = -1;
            }

            Assign(order, DealerSlots, DealerHand);
            Assign(order, SeatOneSlots, SeatOneHand);
            Assign(order, SeatTwoSlots, SeatTwoHand);
            Assign(order, SeatThreeSlots, SeatThreeHand);

            var used = new HashSet<int>();
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] >= 0 && !used.Add(order[i]))
                {
                    throw new InvalidOperationException("C04 cheat scenario assigns a duplicate physical entity.");
                }
            }

            int next = 0;
            for (int slot = 0; slot < order.Length; slot++)
            {
                if (order[slot] >= 0)
                {
                    continue;
                }

                while (used.Contains(next))
                {
                    next++;
                }

                order[slot] = next;
                used.Add(next);
            }

            if (used.Count != TileId.Count)
            {
                throw new InvalidOperationException("C04 cheat scenario did not place all 136 physical entities.");
            }

            return order;
        }

        private static void Assign(int[] order, int[] slots, int[] entities)
        {
            if (slots.Length != entities.Length)
            {
                throw new InvalidOperationException("C04 cheat scenario slot/entity count mismatch.");
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] < 0 || slots[i] >= order.Length)
                {
                    throw new InvalidOperationException("C04 cheat scenario wall slot is out of range.");
                }

                if (entities[i] < 0 || entities[i] >= TileId.Count)
                {
                    throw new InvalidOperationException("C04 cheat scenario entity is out of range.");
                }

                order[slots[i]] = entities[i];
            }
        }

        private static LocalHostEnvironment CreateEnvironment()
        {
            long id = 0;
            var clock = new FixedLocalClock();
            return new LocalHostEnvironment
            {
                Clock = clock,
                NextInt = bound => bound <= 0 ? 0 : 0,
                NextDouble = () => 0.0d,
                NewId = () =>
                {
                    id++;
                    return "c04-" + id.ToString(CultureInfo.InvariantCulture);
                }
            };
        }

        private sealed class FixedLocalClock : Mahjong.TableRuntime.ITableClock
        {
            public long MonotonicMillis => 1_700_000_000_000L;

            public long UtcNowMs => 1_700_000_000_000L;
        }
    }
}
