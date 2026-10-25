using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// N3-AI real-execution binding for tests/v5-golden/ai-v1.json.
    ///
    /// The pre-existing <see cref="AiReplayGoldenTests"/> proves the AI identity, deadline and
    /// intent/rejection mapping and checks that the exporter's before/after summaries are
    /// related to each other. It never executed the accepted action against the Domain round
    /// machine, so an accepted case could only prove fixture self-consistency.
    ///
    /// This test closes that gap:
    /// - every exported case is replayed through <see cref="AiDecisionService"/> admission;
    /// - the single accepted record is passed through the real <see cref="DeadlineArbiter"/>;
    /// - the same accepted record's action is executed on a real <see cref="RoundStateMachine"/>
    ///   built only from the exported AI observation and the documented V4 deal constants;
    /// - every representable exported before/after field and the exported event are compared
    ///   against the real C# state/events.
    ///
    /// Fields V4 exports but C# cannot represent are declared explicitly in
    /// <see cref="UnrepresentableStateFields"/> and guarded by reflection in
    /// <see cref="ExportedStateSchemaIsFullyBoundOrDeclaredUnrepresentable"/>; they are never
    /// silently dropped. No V4 canonical hash exists, so no cross-language hash is compared.
    /// </summary>
    public class AiReplayStateGoldenTests
    {
        // Exact property set/order the exporter writes for a state summary.
        private static readonly string[] StateFields =
        {
            "roundId",
            "roundVersion",
            "phase",
            "currentPlayer",
            "dealerSeat",
            "turnNumber",
            "wallPointer",
            "wallEndExclusive",
            "wallRemaining",
            "riichiSticks",
            "honbaSticks",
            "scores",
            "cheatPoints",
            "lastDiscardTileId",
            "lastDiscardPlayer",
            "seats"
        };

        // Exported fields with a real C# production binding. Every one is compared below.
        private static readonly string[] BoundStateFields =
        {
            "roundId",
            "roundVersion",
            "phase",
            "currentPlayer",
            "dealerSeat",
            "wallPointer",
            "wallEndExclusive",
            "wallRemaining",
            "riichiSticks",
            "honbaSticks",
            "scores",
            "cheatPoints",
            "lastDiscardTileId",
            "lastDiscardPlayer",
            "seats"
        };

        // V4 state fields with no C# Domain port. `turnNumber` is real V4 authority state but
        // RoundStateMachine has no turn counter (see RoundCheatTransition, where a turn number
        // is only an external input). Adding one would be a production scope change.
        private static readonly string[] UnrepresentableStateFields = { "turnNumber" };

        private static readonly string[] SeatFields =
        {
            "seat",
            "closedCount",
            "drawnTileId",
            "meldCount",
            "discardCount",
            "sleeveCount"
        };

        private static readonly string[] EventFields =
        {
            "type",
            "kind",
            "player",
            "tileId",
            "structured",
            "text"
        };

        [Fact]
        public void AcceptedIntentExecutesTheRealRoundAndMatchesExportedState()
        {
            using JsonDocument document = AiGoldenFixture.Load();
            JsonElement root = document.RootElement;
            JsonElement observationElement = root.GetProperty("v5Observation");
            AiObservation observation = AiReplayGoldenTests.BuildObservation(
                observationElement,
                out Dictionary<string, LegalActionOption> actions,
                out List<string> legalRefs);

            JsonElement caseElement = SingleAcceptedCase(root);
            JsonElement v5 = caseElement.GetProperty("v5");
            JsonElement v4 = caseElement.GetProperty("v4");
            JsonElement before = v4.GetProperty("beforeState");
            JsonElement after = v4.GetProperty("afterState");

            CommandIntent intent = AiReplayGoldenTests.BuildIntent(root, observation, actions, caseElement);
            AiDecision replay = AiDecisionService.Replay(intent, observation);
            var service = new AiDecisionService(new ThrowingStrategy());
            long submittedAt = Int64(v5, "submittedAtMillis");
            AiSubmissionResult admitted = service.Submit(
                replay,
                AiReplayGoldenTests.BuildAuthority(caseElement, observation, actions, legalRefs, intent),
                submittedAt);
            Assert.True(admitted.IsAccepted, admitted.Detail);
            Assert.NotNull(admitted.Submission);
            AiDecisionSubmission submission = admitted.Submission!;

            // The real DeadlineArbiter must accept the exact same recorded action (not a copy
            // built from the expected result).
            TickResult tick = Tick(observation, intent, submission, root, submittedAt);
            Assert.Empty(tick.Timeouts);
            Assert.Empty(tick.RejectedAi);
            TickEntry accepted = Assert.Single(tick.AcceptedAi);
            Assert.Same(submission.Action, accepted.Action);
            Assert.Equal(submission.Key.Seat, accepted.Seat);
            Assert.Equal(submission.Key.DecisionId, accepted.DecisionId);
            Assert.Equal(intent.DecisionKey, submission.Key);

            // Build the C# opening state by replaying the real V4 exporter constructor:
            // TestTableBuilder.create reserves the exported hand, then wallPrefix
            // [SOU9=68, WIND2=112], fills seats 0..3 in order to 14/13, takes 14 dead-wall
            // tiles and lets the rest form the live wall. This is source-known input, not a
            // JSON state oracle.
            SourceTable table = BuildInitialRound(observation);
            RoundStateMachine round = table.Round;
            CheatLedger ledger = table.Ledger;
            Assert.Equal(observation.RoundId, round.RoundId);
            Assert.Equal(0L, round.Version);
            Assert.Equal(136, TilePositionMap.Validate(round.Tiles).AssignedCount);

            RoundProjection opening = Project(round, ledger);
            AssertProjectionMatches(before, opening, "AI-001 beforeState");

            // V4 only: the turn counter is exported and a discard must not advance it. C# has
            // no such field, so this is checked on the V4 side and declared unrepresentable.
            int turnBefore = V4TurnNumber(before, "AI-001 beforeState");
            int turnAfter = V4TurnNumber(after, "AI-001 afterState");
            Assert.Equal(turnBefore, turnAfter);

            // Execute the exact record the arbiter accepted, on a real Domain round.
            Assert.Equal(LegalActionKind.Discard, submission.Action.Kind);
            Assert.True(submission.Action.Tile.HasValue, "accepted AI record must carry a physical tile");
            Assert.Equal(observation.Seat, submission.Action.Seat);
            TileId discardedTile = submission.Action.Tile!.Value;
            Assert.Contains(discardedTile, observation.MyHandTileIds);
            Assert.Equal(
                observation.MyHandTileIds,
                round.Tiles.TilesInZone(TileZone.Hand, observation.Seat).ToArray());

            // The response eligibility is derived from the source-reconstructed hands and the
            // V4 rule predicates, never from the exported after.phase or a synthetic seat.
            // Seat 2's exact hand is the TestTableBuilder fill order after reserving hands[1]
            // and wallPrefix [68,112]; this is the constructor output, not a JSON oracle.
            Assert.Equal(
                new[] { 18, 19, 21, 22, 23, 25, 26, 27, 29, 30, 31, 33, 34 },
                table.Hands[2].Select(tile => tile.Value).ToArray());
            Assert.Equal(3, CountSameType(table.Hands[0], discardedTile));
            Assert.Equal(0, CountSameType(table.Hands[2], discardedTile));
            Assert.Equal(0, CountSameType(table.Hands[3], discardedTile));
            Assert.False(CanChi(table.Hands[2], discardedTile));
            IReadOnlyList<int> eligibleSeats = DeriveEligibleResponseSeats(table, submission.Key.Seat, discardedTile);
            Assert.Equal(new[] { 0 }, eligibleSeats.ToArray());
            Assert.Equal(13, table.Hands[0].Count);
            Assert.Equal(13, table.Hands[2].Count);
            Assert.Equal(13, table.Hands[3].Count);

            RoundCommand command = RoundCommand.Discard(
                submission.Key.RoundId,
                submission.Key.Seat,
                discardedTile,
                eligibleSeats);

            RoundTransition transition = round.Execute(command);
            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.NotNull(transition.State);
            RoundStateMachine executed = transition.State!;

            Assert.Equal(1L, executed.Version);
            Assert.Equal(RoundPhase.ResponseWindow, executed.Phase);
            Assert.Equal(observation.Seat, executed.CurrentSeat);
            Assert.Equal(observation.DealerSeat, executed.DealerSeat);
            Assert.Equal(
                TilePosition.Discard(observation.Seat, 0),
                executed.Tiles.PositionOf(discardedTile));
            Assert.NotNull(executed.Window);
            Assert.Equal(observation.Seat, executed.Window!.FromSeat);
            Assert.Equal(discardedTile, executed.Window.TargetTile);
            Assert.False(executed.Window.IsChankan);
            Assert.Equal(eligibleSeats.ToArray(), executed.Window.EligibleSeats.ToArray());
            Assert.True(TilePositionMap.Validate(executed.Tiles).IsValid);
            Assert.Equal(136, TilePositionMap.Validate(executed.Tiles).AssignedCount);

            RoundProjection actual = Project(executed, ledger);
            AssertProjectionMatches(after, actual, "AI-001 afterState");

            // Receipt semantics: V4 APPLIED/no reason <-> C# accepted admission and accepted
            // round transition.
            Assert.Equal("APPLIED", Text(v4, "receiptStatus"));
            Assert.Equal(JsonValueKind.Null, v4.GetProperty("reason").ValueKind);
            Assert.True(Bool(v4, "stateChanged"));
            Assert.True(transition.IsAccepted);

            // Event payload/order. V4 exports exactly one committed receipt event; C# commits
            // that same event plus its explicit structural ResponseWindowOpened event. V4 has
            // no window-open event in its receipt list (RoundTransitionPlanner only appends
            // TileDiscarded when eligible seats exist), so the exact C# sequence is asserted
            // and the first element is compared field-by-field with the exported event.
            JsonElement v4Events = v4.GetProperty("events");
            Assert.Equal(1, v4Events.GetArrayLength());
            JsonElement recordedEvent = v4Events[0];
            AssertExactProperties(recordedEvent, "AI-001 v4.events[0]", EventFields);
            Assert.Equal("TileDiscarded", Text(recordedEvent, "type"));
            Assert.Equal("Discard", Text(recordedEvent, "kind"));
            Assert.True(Bool(recordedEvent, "structured"));
            Assert.False(string.IsNullOrWhiteSpace(Text(recordedEvent, "text")));
            Assert.Equal(Int(v4, "eventCount"), v4Events.GetArrayLength());

            RoundEvent[] committed = transition.Events.ToArray();
            Assert.Equal(
                new[] { RoundEventType.TileDiscarded, RoundEventType.ResponseWindowOpened },
                committed.Select(roundEvent => roundEvent.Type).ToArray());
            Assert.Equal(new[] { 0, 1 }, committed.Select(roundEvent => roundEvent.Ordinal).ToArray());
            Assert.Equal(Int(recordedEvent, "player"), committed[0].Seat);
            Assert.Equal(Int(recordedEvent, "tileId"), committed[0].Tile!.Value.Value);
            Assert.Equal(discardedTile.Value, committed[0].Tile!.Value.Value);
            Assert.Equal(committed[0].Seat, committed[1].Seat);
            Assert.Equal(committed[0].Tile, committed[1].Tile);
            Assert.Equal(2, executed.Events.Count);
            Assert.Equal(2, committed.Length);
        }

        [Fact]
        public void RejectedIntentsHaveZeroRealRoundSideEffects()
        {
            using JsonDocument document = AiGoldenFixture.Load();
            JsonElement root = document.RootElement;
            JsonElement observationElement = root.GetProperty("v5Observation");
            AiObservation observation = AiReplayGoldenTests.BuildObservation(
                observationElement,
                out Dictionary<string, LegalActionOption> actions,
                out List<string> legalRefs);

            SourceTable table = BuildInitialRound(observation);
            RoundStateMachine round = table.Round;
            CheatLedger ledger = table.Ledger;
            ulong tilesHash = round.Tiles.StateHash;
            ulong sourceFingerprint = round.SourceFingerprint;
            long version = round.Version;
            int eventCount = round.Events.Count;
            long cheatLedgerVersion = ledger.Version;

            var service = new AiDecisionService(new ThrowingStrategy());
            int rejectedCases = 0;
            int acceptedCases = 0;
            foreach (JsonElement caseElement in root.GetProperty("cases").EnumerateArray())
            {
                string id = Text(caseElement, "id");
                JsonElement v5 = caseElement.GetProperty("v5");
                JsonElement expected = v5.GetProperty("expected");
                JsonElement v4 = caseElement.GetProperty("v4");
                JsonElement before = v4.GetProperty("beforeState");
                JsonElement after = v4.GetProperty("afterState");

                if (Bool(expected, "accepted"))
                {
                    acceptedCases++;
                    continue;
                }

                rejectedCases++;
                CommandIntent intent = AiReplayGoldenTests.BuildIntent(root, observation, actions, caseElement);
                AiDecision replay = AiDecisionService.Replay(intent, observation);
                AiSubmissionResult result = service.Submit(
                    replay,
                    AiReplayGoldenTests.BuildAuthority(caseElement, observation, actions, legalRefs, intent),
                    Int64(v5, "submittedAtMillis"));

                Assert.False(result.IsAccepted, id + " must be rejected");
                Assert.Equal(Text(expected, "rejectionCode"), result.Code.ToString());
                Assert.Null(result.Submission);

                // V4 receipt semantics for every rejection.
                Assert.Equal("REJECTED", Text(v4, "receiptStatus"));
                Assert.Equal("STALE_AI_SUBMISSION", Text(v4, "reason"));
                Assert.False(Bool(v4, "stateChanged"));
                Assert.Equal(0, Int(v4, "eventCount"));
                Assert.Equal(0, v4.GetProperty("events").GetArrayLength());
                Assert.True(
                    AiReplayGoldenTests.DeepEquals(before, after),
                    id + ": a rejected V4 call must export identical before/after states");

                // The real Domain state, its fingerprint, version, event log and window are
                // untouched: a rejected admission carries no submission, so nothing can be
                // executed against the round.
                Assert.Equal(version, round.Version);
                Assert.Equal(tilesHash, round.Tiles.StateHash);
                Assert.Equal(sourceFingerprint, round.SourceFingerprint);
                Assert.Equal(eventCount, round.Events.Count);
                Assert.Null(round.Window);
                Assert.Null(round.Result);
                Assert.Equal(cheatLedgerVersion, ledger.Version);

                RoundProjection current = Project(round, ledger);
                AssertProjectionMatches(before, current, id + " beforeState");
                AssertProjectionMatches(after, current, id + " afterState");
            }

            Assert.Equal(6, rejectedCases);
            Assert.Equal(1, acceptedCases);
        }

        [Fact]
        public void ExportedStateSchemaIsFullyBoundOrDeclaredUnrepresentable()
        {
            using JsonDocument document = AiGoldenFixture.Load();
            JsonElement root = document.RootElement;

            Assert.Equal(AiGoldenFixture.ExpectedSchema, Text(root, "schema"));
            Assert.Equal(7, root.GetProperty("cases").GetArrayLength());
            Assert.Equal(
                "AI-001-legal-intent-before-deadline",
                Text(root.GetProperty("cases").EnumerateArray().Last(), "id"));

            // No binding may be silently added without updating the compare routine: the
            // exact exported field set must equal the explicitly bound set plus the declared
            // unrepresentable set.
            Assert.Equal(StateFields.Length, BoundStateFields.Length + UnrepresentableStateFields.Length);
            foreach (string field in StateFields)
            {
                Assert.True(
                    BoundStateFields.Contains(field) ^ UnrepresentableStateFields.Contains(field),
                    "exported state field '" + field + "' must be exactly one of bound or declared unrepresentable");
            }

            Assert.DoesNotContain("turnNumber", BoundStateFields);
            Assert.Single(UnrepresentableStateFields);

            int stateCount = 0;
            int seatCount = 0;
            int eventCount = 0;
            foreach (JsonElement caseElement in root.GetProperty("cases").EnumerateArray())
            {
                string id = Text(caseElement, "id");
                JsonElement v4 = caseElement.GetProperty("v4");
                foreach (string stateName in new[] { "beforeState", "afterState" })
                {
                    JsonElement state = v4.GetProperty(stateName);
                    AssertExactProperties(state, id + " " + stateName, StateFields);
                    JsonElement seats = state.GetProperty("seats");
                    Assert.Equal(4, seats.GetArrayLength());
                    int index = 0;
                    foreach (JsonElement seat in seats.EnumerateArray())
                    {
                        AssertExactProperties(seat, id + " " + stateName + ".seats[" + index + "]", SeatFields);
                        Assert.Equal(index, Int(seat, "seat"));
                        index++;
                    }

                    // Declared-unrepresentable fields are still checked for the V4-side
                    // invariants we can state without a C# oracle.
                    V4TurnNumber(state, id + " " + stateName);
                    seatCount += seats.GetArrayLength();
                    stateCount++;
                }

                foreach (JsonElement recordedEvent in v4.GetProperty("events").EnumerateArray())
                {
                    AssertExactProperties(recordedEvent, id + " v4.events[" + eventCount + "]", EventFields);
                    eventCount++;
                }
            }

            Assert.Equal(14, stateCount);
            Assert.Equal(1, eventCount);
            Assert.Equal(56, seatCount);

            // The declared gap is a real absence of a production port, not a skipped field.
            Assert.True(
                typeof(RoundStateMachine).GetProperty("TurnNumber") == null,
                "RoundStateMachine unexpectedly gained TurnNumber; bind turnNumber instead of declaring it unrepresentable");
            Assert.Equal(
                new[] { "turnNumber" },
                UnrepresentableStateFields);
            Assert.NotNull(typeof(RoundStateMachine).GetProperty("Version"));
            Assert.NotNull(typeof(RoundStateMachine).GetProperty("CurrentSeat"));
            Assert.NotNull(typeof(RoundStateMachine).GetProperty("Phase"));
            Assert.Null(typeof(RoundStateMachine).GetProperty("CheatPoints"));
            Assert.NotNull(typeof(CheatLedger).GetProperty("Balances"));

            // No V4 canonical/state hash oracle exists in this fixture; asserting equality of
            // hashes would be fabricated. The C# tile hash is only used for zero-side-effect
            // checks inside this test.
            string[] forbiddenHashFields = { "stateHash", "tilesStateHash", "canonicalHash", "v4StateHash", "aiHash" };
            foreach (string propertyName in root.EnumerateObject().Select(property => property.Name))
            {
                Assert.DoesNotContain(propertyName, forbiddenHashFields);
            }

            JsonElement samples = root.GetProperty("unexportedOrMappedSamples");
            Assert.True(
                samples.EnumerateArray().Any(sample => (sample.GetString() ?? string.Empty)
                    .Contains("cross-language canonical hash equality", StringComparison.Ordinal)),
                "the fixture must keep the explicit no-cross-language-hash limit");
        }

        private static JsonElement SingleAcceptedCase(JsonElement root)
        {
            JsonElement[] accepted = root.GetProperty("cases").EnumerateArray()
                .Where(caseElement => Bool(caseElement.GetProperty("v5").GetProperty("expected"), "accepted"))
                .ToArray();
            Assert.Single(accepted);
            return accepted[0];
        }

        /// <summary>
        /// V4 response eligibility, derived from the source-reconstructed hands and the frozen
        /// V4 predicates (not from after.phase and not from a hand-picked non-empty list):
        /// - ron: a structurally complete hand is a necessary condition
        ///   (HandDecomposer.CanWin). V4 additionally requires a legal yaku and no furiten
        ///   (LegalActionProvider.kt:168-192); no seat is structurally complete for this
        ///   fixture, so structural false is a decisive "no ron". If a seat ever were
        ///   complete, this throws rather than pretending the yaku/furiten port exists.
        /// - pon: RiichiRuleEngine.canPon (LegalActionProvider.kt:197-201);
        /// - open kan: RiichiRuleEngine.canOpenKan plus the wall/rinshan/kan guards
        ///   (LegalActionProvider.kt:204-209);
        /// - chi: only (discarder+1)%4 and RiichiRuleEngine.canChi
        ///   (LegalActionProvider.kt:212-217, RiichiRuleEngine.kt:146-...).
        /// No seat is riichi, so the V4 riichi-call prohibition (LegalActionProvider.kt:195)
        /// is vacuously satisfied.
        /// </summary>
        private static IReadOnlyList<int> DeriveEligibleResponseSeats(
            SourceTable table,
            int discarder,
            TileId discard)
        {
            var eligible = new List<int>();
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == discarder)
                {
                    continue;
                }

                IReadOnlyList<TileId> hand = table.Hands[seat];
                Assert.Equal(13, hand.Count);
                int copies = CountSameType(hand, discard);

                bool pon = copies >= 2;
                bool openKan =
                    copies >= 3 &&
                    table.Round.Tiles.WallRemaining > 0 &&
                    table.Round.Tiles.RinshanPointer < 4 &&
                    table.Round.Tiles.KanCount < 4;
                bool chi = seat == (discarder + 1) % 4 && CanChi(hand, discard);

                // V4 requires yaku/furiten for a ron. HandDecomposer only proves structural
                // completeness; a structural win would need the missing yaku/furiten port.
                if (HandDecomposer.CanWin(hand.Append(discard)))
                {
                    throw new Xunit.Sdk.XunitException(
                        "seat " + seat + " is structurally complete on " + discard +
                        "; V4 ron eligibility additionally needs yaku/furiten evaluation, which is not ported here. " +
                        "Declare the BLOCKED range instead of guessing eligibility.");
                }

                if (pon || openKan || chi)
                {
                    eligible.Add(seat);
                }
            }

            return eligible;
        }

        private static bool CanChi(IReadOnlyList<TileId> hand, TileId discard)
        {
            if (!discard.IsNumberTile)
            {
                return false;
            }

            bool HasRank(int rank) => hand.Any(tile =>
                tile.IsNumberTile && tile.Suit == discard.Suit && tile.Rank == rank);

            int rank = discard.Rank;
            return (rank <= 7 && HasRank(rank + 1) && HasRank(rank + 2)) ||
                   (rank >= 2 && rank <= 8 && HasRank(rank - 1) && HasRank(rank + 1)) ||
                   (rank >= 3 && HasRank(rank - 2) && HasRank(rank - 1));
        }

        private static int CountSameType(IReadOnlyList<TileId> hand, TileId tile) =>
            hand.Count(candidate => candidate.TypeIndex == tile.TypeIndex);

        private static TickResult Tick(
            AiObservation observation,
            CommandIntent intent,
            AiDecisionSubmission submission,
            JsonElement root,
            long submittedAtMillis)
        {
            JsonElement clockElement = root.GetProperty("decisionClock");
            ReserveScope reserveScope = Text(clockElement, "reserveScope") switch
            {
                "ROUND" => ReserveScope.Round,
                "SESSION" => ReserveScope.Session,
                string other => throw new InvalidOperationException("Unknown reserve scope: " + other)
            };
            var clock = new DecisionClock(new DecisionClockConfig(
                Int64(clockElement, "baseMillis"),
                Int64(clockElement, "reserveMillis"),
                reserveScope));
            DecisionTiming timing = clock.Open(intent.DecisionKey, Int64(clockElement, "opensAtMillis"));
            Assert.Equal(Int64(clockElement, "deadlineMillis"), timing.DeadlineMillis);

            // V4 initialized seat 1 as Computer, which increments its ControlRegistry epoch.
            var control = new ControlRegistry();
            control.Initialize(new[]
            {
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.Computer,
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.RemoteHuman
            });
            Assert.Equal(intent.ControlEpoch, control.GetEpoch(intent.Seat));

            var pending = new PendingDecision(
                intent.DecisionKey,
                intent.ControlEpoch,
                observation.LegalActions,
                observation.MyDrawnTileId);
            return new DeadlineArbiter(clock, control).ProcessTick(new TickInput(
                observation.RoundId,
                submittedAtMillis,
                new[] { pending },
                new[] { submission }));
        }

        private sealed class SourceTable
        {
            public SourceTable(
                RoundStateMachine round,
                CheatLedger ledger,
                IReadOnlyList<TileId>[] hands,
                IReadOnlyList<TileId> wallPrefix)
            {
                Round = round;
                Ledger = ledger;
                Hands = hands;
                WallPrefix = wallPrefix;
            }

            public RoundStateMachine Round { get; }

            public CheatLedger Ledger { get; }

            public IReadOnlyList<TileId>[] Hands { get; }

            public IReadOnlyList<TileId> WallPrefix { get; }
        }

        /// <summary>
        /// Reconstructs the V4 opening table by replaying the real exporter constructor
        /// (V5AiGoldenExporterTest.kt:99-124 + TestTableBuilder.kt:20-76) instead of filling
        /// the unexported containers from an arbitrary complement:
        /// 1. TilePool.buildStandardSet() is typeIndex 0..33 x copyIndex 0..3, i.e. ascending
        ///    physical TileId 0..135;
        /// 2. TestTableBuilder reserves hands[1] (the exported 14 ids), then wallPrefix
        ///    [SOU9 copy0 = 68, WIND2 copy0 = 112];
        /// 3. seats 0..3 are filled in source order to 14 for the DISCARD actor and 13 for the
        ///    other seats, taking remaining tiles from the front;
        /// 4. the next 14 tiles become the dead wall and the rest follows wallPrefix in the
        ///    live wall (V4 array index 0 -> C# absolute slot 53).
        ///
        /// These identities are source-known inputs, not values read from the expected after
        /// state and not a claim that the JSON summary contains a full-table oracle.
        /// </summary>
        private static SourceTable BuildInitialRound(AiObservation observation)
        {
            Assert.Equal(observation.DealerSeat, observation.Seat);
            Assert.Equal(RoundPhase.Discard, observation.Phase);
            Assert.Null(observation.MyDrawnTileId);
            Assert.Empty(observation.MyMelds);
            Assert.Empty(observation.VisibleMelds);
            Assert.Empty(observation.VisibleDiscards);
            Assert.Empty(observation.VisibleDoraIndicators);
            Assert.Equal(14, observation.MyHandTileIds.Count);

            var remaining = Enumerable.Range(0, TileId.Count)
                .Select(value => new TileId(value))
                .ToList();

            void Reserve(TileId tile, string source)
            {
                Assert.True(
                    remaining.Remove(tile),
                    "source table tile " + tile + " is reserved twice or missing (reservation: " + source + ")");
            }

            foreach (TileId tile in observation.MyHandTileIds)
            {
                Reserve(tile, "hands[1]");
            }

            // V4 exporter wallPrefix = [Tile(SOU, 9, copy0), Tile(WIND, 2, copy0)].
            TileId[] wallPrefix = { new TileId(68), new TileId(112) };
            foreach (TileId tile in wallPrefix)
            {
                Reserve(tile, "wallPrefix");
            }

            var hands = new List<TileId>[4];
            for (int seat = 0; seat < 4; seat++)
            {
                hands[seat] = new List<TileId>();
            }

            hands[observation.Seat].AddRange(observation.MyHandTileIds);
            for (int seat = 0; seat < 4; seat++)
            {
                int target = seat == observation.Seat ? 14 : 13;
                while (hands[seat].Count < target)
                {
                    hands[seat].Add(remaining[0]);
                    remaining.RemoveAt(0);
                }

                Assert.Equal(target, hands[seat].Count);
            }

            var deadWall = remaining.Take(TilePositionMap.DeadWallSlotCount).ToList();
            remaining.RemoveRange(0, TilePositionMap.DeadWallSlotCount);
            var wall = wallPrefix.Concat(remaining).ToList();

            Assert.Equal(observation.RemainingWallCount, wall.Count);
            Assert.Equal(69, wall.Count);
            Assert.Equal(new[] { 68, 112 }, wall.Take(2).Select(tile => tile.Value).ToArray());

            var used = new bool[TileId.Count];
            var placements = new List<TilePlacement>(TileId.Count);

            void Place(TileId tile, TilePosition position)
            {
                Assert.False(used[tile.Value], "tile " + tile + " was placed twice");
                used[tile.Value] = true;
                placements.Add(new TilePlacement(tile, position));
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int index = 0; index < hands[seat].Count; index++)
                {
                    Place(hands[seat][index], TilePosition.Hand(seat, index));
                }
            }

            for (int index = 0; index < deadWall.Count; index++)
            {
                Place(deadWall[index], TilePosition.DeadWall(index));
            }

            for (int index = 0; index < wall.Count; index++)
            {
                Place(
                    wall[index],
                    TilePosition.Wall(TilePositionMap.InitialDealCount + index));
            }

            Assert.Equal(TileId.Count, placements.Count);
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                TilePositionMap.InitialDealCount,
                TilePositionMap.InitialDealCount + wall.Count,
                0,
                doraIndicators: Array.Empty<TileId>());
            Assert.True(TilePositionMap.Validate(map).IsValid);

            var spec = new RoundSpec(
                observation.RoundId,
                observation.DealerSeat,
                observation.RoundWind,
                observation.RoundNumber,
                observation.HonbaSticks,
                observation.Scores,
                observation.RiichiSticks);
            RoundStateMachine round = RoundStateMachine.CreateDealt(spec, map, observation.Phase);

            // V4 GameState.cheatPoints is IntArray(4) { 0 } and the default CheatEconomyConfig
            // initialises every seat to 0 points. C# keeps cheat points in a separate immutable
            // ledger; the round state itself has no cheat-point field.
            CheatLedger ledger = CheatLedger.Initialize(new CheatEconomyConfig(
                enabled: false,
                initialPoints: 0,
                costs: new Dictionary<string, int>(StringComparer.Ordinal)));
            Assert.Equal(new[] { 0, 0, 0, 0 }, ledger.Balances.ToArray());

            var handViews = new IReadOnlyList<TileId>[4];
            for (int seat = 0; seat < 4; seat++)
            {
                handViews[seat] = hands[seat];
            }

            return new SourceTable(round, ledger, handViews, wallPrefix);
        }

        private static RoundProjection Project(RoundStateMachine round, CheatLedger ledger)
        {
            // V4 indexes its live-wall array from 0; C# uses absolute slots with the standard
            // 53-tile deal offset (TilePositionMap.InitialDealCount).
            int wallPointer = round.Tiles.WallPointer - TilePositionMap.InitialDealCount;
            int wallEndExclusive = round.Tiles.WallEndExclusive - TilePositionMap.InitialDealCount;
            Assert.True(wallPointer >= 0, "C# wall pointer is before the standard deal offset");
            Assert.True(wallEndExclusive >= wallPointer, "C# wall end is before its pointer");

            // This fixture has no wall holes; physical remaining must equal the span so the
            // V4 (endExclusive - pointer) representation is a faithful projection.
            Assert.Equal(wallEndExclusive - wallPointer, round.Tiles.WallRemaining);

            var seats = new SeatProjection[4];
            for (int seat = 0; seat < 4; seat++)
            {
                DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
                seats[seat] = new SeatProjection(
                    seat,
                    round.Tiles.CountAt(TileZone.Hand, seat),
                    drawn.HasValue && drawn.Value.Seat == seat ? drawn.Value.Tile.Value : (int?)null,
                    round.Melds.Count(meld => meld.Seat == seat),
                    round.Tiles.CountAt(TileZone.Discard, seat),
                    round.Tiles.CountAt(TileZone.Sleeve, seat));
            }

            LastDiscardEvidence? lastDiscard = round.Tiles.LastDiscard;
            return new RoundProjection(
                round.RoundId,
                round.Version,
                round.Phase,
                round.CurrentSeat,
                round.DealerSeat,
                wallPointer,
                wallEndExclusive,
                round.Tiles.WallRemaining,
                round.RiichiSticks,
                round.HonbaSticks,
                round.Scores.ToArray(),
                ledger.Balances.ToArray(),
                lastDiscard?.Tile.Value,
                lastDiscard?.Seat ?? -1,
                seats);
        }

        private static void AssertProjectionMatches(JsonElement expected, RoundProjection actual, string context)
        {
            AssertExactProperties(expected, context, StateFields);

            Assert.Equal(Text(expected, "roundId"), actual.RoundId);
            Assert.Equal(Int64(expected, "roundVersion"), actual.RoundVersion);
            Assert.Equal(ParsePhase(Text(expected, "phase")), actual.Phase);
            Assert.Equal(Int(expected, "currentPlayer"), actual.CurrentPlayer);
            Assert.Equal(Int(expected, "dealerSeat"), actual.DealerSeat);
            Assert.Equal(Int(expected, "wallPointer"), actual.WallPointer);
            Assert.Equal(Int(expected, "wallEndExclusive"), actual.WallEndExclusive);
            Assert.Equal(Int(expected, "wallRemaining"), actual.WallRemaining);
            Assert.Equal(Int(expected, "riichiSticks"), actual.RiichiSticks);
            Assert.Equal(Int(expected, "honbaSticks"), actual.HonbaSticks);
            Assert.Equal(LongArray(expected.GetProperty("scores")), actual.Scores);
            Assert.Equal(
                LongArray(expected.GetProperty("cheatPoints")),
                actual.CheatPoints.Select(points => (long)points).ToArray());

            int? lastDiscardTileId = TileIdOrNull(expected, "lastDiscardTileId")?.Value;
            Assert.Equal(lastDiscardTileId, actual.LastDiscardTileId);
            Assert.Equal(Int(expected, "lastDiscardPlayer"), actual.LastDiscardPlayer);

            JsonElement seats = expected.GetProperty("seats");
            Assert.Equal(4, seats.GetArrayLength());
            int index = 0;
            foreach (JsonElement seatElement in seats.EnumerateArray())
            {
                AssertExactProperties(seatElement, context + ".seats[" + index + "]", SeatFields);
                SeatProjection seat = actual.Seats[index];
                Assert.Equal(Int(seatElement, "seat"), seat.Seat);
                Assert.Equal(Int(seatElement, "closedCount"), seat.ClosedCount);
                Assert.Equal(TileIdOrNull(seatElement, "drawnTileId")?.Value, seat.DrawnTileId);
                Assert.Equal(Int(seatElement, "meldCount"), seat.MeldCount);
                Assert.Equal(Int(seatElement, "discardCount"), seat.DiscardCount);
                Assert.Equal(Int(seatElement, "sleeveCount"), seat.SleeveCount);
                Assert.Equal(index, seat.Seat);
                index++;
            }
        }

        private static RoundPhase ParsePhase(string value) => value switch
        {
            "STACKING" => RoundPhase.Stacking,
            "DEALING" => RoundPhase.Dealing,
            "DRAW" => RoundPhase.Draw,
            "CHEAT_WINDOW" => RoundPhase.CheatWindow,
            "DISCARD" => RoundPhase.Discard,
            "RESPONSE_WINDOW" => RoundPhase.ResponseWindow,
            "CHANKAN_WINDOW" => RoundPhase.ChankanWindow,
            "KAN_DRAW" => RoundPhase.KanDraw,
            "ROUND_END" => RoundPhase.RoundEnd,
            string other => throw new InvalidOperationException("Unknown exported round phase: " + other)
        };

        private static void AssertExactProperties(JsonElement element, string context, string[] expected)
        {
            Assert.Equal(JsonValueKind.Object, element.ValueKind);
            string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(expected, actual);
        }

        private static long[] LongArray(JsonElement array) =>
            array.EnumerateArray().Select(value => value.GetInt64()).ToArray();

        private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

        private static long Int64(JsonElement element, string name) => element.GetProperty(name).GetInt64();

        /// <summary>
        /// `turnNumber` has no C# round-state port, but the V4 value is still a Kotlin Int.
        /// Number-kind alone is not enough: 12.5 and 1e30 are JSON numbers but not V4 Ints.
        /// There is deliberately no fallback to a default or truncated value.
        /// </summary>
        private static int V4TurnNumber(JsonElement state, string context)
        {
            JsonElement value = state.GetProperty("turnNumber");
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            if (!value.TryGetInt32(out int turn))
            {
                throw new Xunit.Sdk.XunitException(
                    context + ".turnNumber must be a V4 Int32, raw=" + value.GetRawText());
            }

            if (turn < 0)
            {
                throw new Xunit.Sdk.XunitException(
                    context + ".turnNumber must be non-negative, was " + turn);
            }

            return turn;
        }

        private static bool Bool(JsonElement element, string name) => element.GetProperty(name).GetBoolean();

        private static TileId? TileIdOrNull(JsonElement element, string name)
        {
            JsonElement value = element.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? (TileId?)null : new TileId(value.GetInt32());
        }

        private static string Text(JsonElement element, string name)
        {
            JsonElement value = element.GetProperty(name);
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            string text = value.GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(text), "empty string field: " + name);
            return text;
        }

        private sealed class SeatProjection
        {
            public SeatProjection(
                int seat,
                int closedCount,
                int? drawnTileId,
                int meldCount,
                int discardCount,
                int sleeveCount)
            {
                Seat = seat;
                ClosedCount = closedCount;
                DrawnTileId = drawnTileId;
                MeldCount = meldCount;
                DiscardCount = discardCount;
                SleeveCount = sleeveCount;
            }

            public int Seat { get; }

            public int ClosedCount { get; }

            public int? DrawnTileId { get; }

            public int MeldCount { get; }

            public int DiscardCount { get; }

            public int SleeveCount { get; }
        }

        private sealed class RoundProjection
        {
            public RoundProjection(
                string roundId,
                long roundVersion,
                RoundPhase phase,
                int currentPlayer,
                int dealerSeat,
                int wallPointer,
                int wallEndExclusive,
                int wallRemaining,
                int riichiSticks,
                int honbaSticks,
                long[] scores,
                int[] cheatPoints,
                int? lastDiscardTileId,
                int lastDiscardPlayer,
                SeatProjection[] seats)
            {
                RoundId = roundId;
                RoundVersion = roundVersion;
                Phase = phase;
                CurrentPlayer = currentPlayer;
                DealerSeat = dealerSeat;
                WallPointer = wallPointer;
                WallEndExclusive = wallEndExclusive;
                WallRemaining = wallRemaining;
                RiichiSticks = riichiSticks;
                HonbaSticks = honbaSticks;
                Scores = scores;
                CheatPoints = cheatPoints;
                LastDiscardTileId = lastDiscardTileId;
                LastDiscardPlayer = lastDiscardPlayer;
                Seats = seats;
            }

            public string RoundId { get; }

            public long RoundVersion { get; }

            public RoundPhase Phase { get; }

            public int CurrentPlayer { get; }

            public int DealerSeat { get; }

            public int WallPointer { get; }

            public int WallEndExclusive { get; }

            public int WallRemaining { get; }

            public int RiichiSticks { get; }

            public int HonbaSticks { get; }

            public long[] Scores { get; }

            public int[] CheatPoints { get; }

            public int? LastDiscardTileId { get; }

            public int LastDiscardPlayer { get; }

            public SeatProjection[] Seats { get; }
        }

        private sealed class ThrowingStrategy : IAiStrategy
        {
            public string AlgorithmId => "must-not-run";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, System.Threading.CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Golden replay/admission must not run a strategy.");
        }
    }
}
