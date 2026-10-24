using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    public sealed class PublicSessionHistoryTests
    {
        private static readonly long[] DefaultScores = { 25000, 25000, 25000, 25000 };
        private static readonly long[] DefaultDeltas = { 0, 0, 0, 0 };

        [Fact]
        public void RoundRecordStoresRealPublicValuesBySeatWithoutRepricing()
        {
            var shuffledScores = new[]
            {
                new ScoreView(2, 24000),
                new ScoreView(0, 26000),
                new ScoreView(3, 25000),
                new ScoreView(1, 25000)
            };
            var shuffledDeltas = new[]
            {
                new ScoreView(3, 0),
                new ScoreView(1, 0),
                new ScoreView(0, 1000),
                new ScoreView(2, -1000)
            };

            var record = new PublicRoundRecord(
                new RoundId("round-17"),
                "result-42",
                shuffledScores,
                shuffledDeltas);

            Assert.Equal("round-17", record.RoundId.Value);
            Assert.Equal("result-42", record.ResultId);

            long[] expectedScores = { 26000, 25000, 24000, 25000 };
            long[] expectedDeltas = { 1000, 0, -1000, 0 };
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(seat, record.FinalScores[seat].Seat);
                Assert.Equal(expectedScores[seat], record.FinalScores[seat].Score);
                Assert.Equal(seat, record.Deltas[seat].Seat);
                Assert.Equal(expectedDeltas[seat], record.Deltas[seat].Score);
            }
        }

        [Fact]
        public void RoundRecordAcceptsNegativeScoresAndRiichiDepositDeltas()
        {
            var negativeScore = new PublicRoundRecord(
                new RoundId("round-negative"),
                "result-negative",
                SeatRows(new long[] { -1000, 24000, 25000, 27000 }),
                SeatRows(new long[] { -26000, 0, 0, 26000 }));

            Assert.Equal(-1000, negativeScore.FinalScores[0].Score);
            Assert.Equal(-26000, negativeScore.Deltas[0].Score);
            Assert.Equal(0, negativeScore.Deltas.Sum(row => row.Score));

            var riichiDeposit = new PublicRoundRecord(
                new RoundId("round-riichi"),
                "result-riichi",
                SeatRows(new long[] { 24000, 25000, 25000, 25000 }),
                SeatRows(new long[] { -1000, 0, 0, 0 }));

            Assert.Equal(-1000, riichiDeposit.Deltas.Sum(row => row.Score));
            Assert.Equal(4, riichiDeposit.Deltas.Count);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        public void RoundRecordRequiresExactlyFourFinalScoreRows(int count)
        {
            var rows = RowsCyclingSeats(count);
            var good = SeatRows(DefaultScores);

            var exception = Assert.Throws<ArgumentException>(() => new PublicRoundRecord(
                new RoundId("round-count-" + count),
                "result-count-" + count,
                rows,
                good));

            Assert.Equal("finalScores", exception.ParamName);
            Assert.Contains("exactly four seat rows", exception.Message);
        }

        [Fact]
        public void RoundRecordAppliesTheSameFourRowRuleToDeltas()
        {
            var good = SeatRows(DefaultScores);
            var bad = new[]
            {
                new ScoreView(0, 0),
                new ScoreView(1, 0),
                new ScoreView(2, 0)
            };

            var exception = Assert.Throws<ArgumentException>(() => new PublicRoundRecord(
                new RoundId("round-delta-count"),
                "result-delta-count",
                good,
                bad));

            Assert.Equal("deltas", exception.ParamName);
            Assert.Contains("exactly four seat rows", exception.Message);
        }

        [Fact]
        public void RoundRecordRejectsDuplicateSeatRows()
        {
            var duplicate = new[]
            {
                new ScoreView(0, 1),
                new ScoreView(1, 1),
                new ScoreView(2, 1),
                new ScoreView(2, 2)
            };
            var good = SeatRows(DefaultScores);

            var scoreException = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-duplicate-score"), "result-1", duplicate, good));
            Assert.Equal("finalScores", scoreException.ParamName);
            Assert.Contains("duplicate seat 2", scoreException.Message);

            var deltaException = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-duplicate-delta"), "result-2", good, duplicate));
            Assert.Equal("deltas", deltaException.ParamName);
            Assert.Contains("duplicate seat 2", deltaException.Message);
        }

        [Fact]
        public void RoundRecordRejectsNullListsAndNullRows()
        {
            var good = SeatRows(DefaultScores);

            var nullScores = Assert.Throws<ArgumentNullException>(() =>
                new PublicRoundRecord(new RoundId("round-null-scores"), "result-1", null!, good));
            Assert.Equal("finalScores", nullScores.ParamName);

            var nullDeltas = Assert.Throws<ArgumentNullException>(() =>
                new PublicRoundRecord(new RoundId("round-null-deltas"), "result-2", good, null!));
            Assert.Equal("deltas", nullDeltas.ParamName);

            var rowsWithNull = new[] { new ScoreView(0, 1), null!, new ScoreView(2, 1), new ScoreView(3, 1) };
            var scoreRow = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-null-row"), "result-3", rowsWithNull, good));
            Assert.Equal("finalScores", scoreRow.ParamName);
            Assert.Contains("null rows", scoreRow.Message);

            var deltaRow = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-null-row-2"), "result-4", good, rowsWithNull));
            Assert.Equal("deltas", deltaRow.ParamName);
            Assert.Contains("null rows", deltaRow.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(" result")]
        [InlineData("result ")]
        [InlineData("res\nult")]
        public void RoundRecordRejectsBlankOrControlResultIds(string? resultId)
        {
            var good = SeatRows(DefaultScores);

            if (resultId == null)
            {
                var nullException = Assert.Throws<ArgumentNullException>(() =>
                    new PublicRoundRecord(new RoundId("round-result-null"), resultId!, good, good));
                Assert.Equal("resultId", nullException.ParamName);
                return;
            }

            var exception = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-result-bad"), resultId, good, good));
            Assert.Equal("resultId", exception.ParamName);
        }

        [Fact]
        public void RoundRecordRejectsOversizedResultId()
        {
            var good = SeatRows(DefaultScores);
            string oversized = new string('x', ProtocolConstants.MaxIdChars + 1);

            var exception = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(new RoundId("round-result-long"), oversized, good, good));

            Assert.Equal("resultId", exception.ParamName);
            Assert.Contains("exceeds", exception.Message);
        }

        [Fact]
        public void RoundRecordRejectsDefaultRoundId()
        {
            var good = SeatRows(DefaultScores);

            var exception = Assert.Throws<ArgumentException>(() =>
                new PublicRoundRecord(default(RoundId), "result-default-round", good, good));

            Assert.Equal("roundId", exception.ParamName);
            Assert.Contains("must not be empty", exception.Message);
        }

        [Fact]
        public void RoundRecordSnapshotsInputListsAndRejectsWrites()
        {
            var scores = new List<ScoreView>(SeatRows(new long[] { 26000, 25000, 24000, 25000 }));
            var deltas = new List<ScoreView>(SeatRows(new long[] { 1000, 0, -1000, 0 }));
            var record = new PublicRoundRecord(
                new RoundId("round-frozen"),
                "result-frozen",
                scores,
                deltas);

            scores[0] = new ScoreView(0, 99999);
            scores.Clear();
            deltas[0] = new ScoreView(0, -99999);
            deltas.Clear();

            Assert.Equal(26000, record.FinalScores[0].Score);
            Assert.Equal(1000, record.Deltas[0].Score);
            Assert.Equal(4, record.FinalScores.Count);
            Assert.Equal(4, record.Deltas.Count);

            var writableScores = Assert.IsAssignableFrom<IList<ScoreView>>(record.FinalScores);
            Assert.True(writableScores.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writableScores[0] = new ScoreView(0, 1));
            Assert.Throws<NotSupportedException>(() => writableScores.Clear());
            Assert.Throws<NotSupportedException>(() => writableScores.Add(new ScoreView(0, 1)));

            var writableDeltas = Assert.IsAssignableFrom<IList<ScoreView>>(record.Deltas);
            Assert.True(writableDeltas.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writableDeltas[0] = new ScoreView(0, 1));
            Assert.Throws<NotSupportedException>(() => writableDeltas.Clear());
        }

        [Fact]
        public void HistoryPreservesCallerOrderWithoutSortingOrDeduplicating()
        {
            var first = Record("round-10", "result-10");
            var second = Record("round-2", "result-2");
            var third = Record("round-1", "result-1");

            var history = new PublicSessionHistory(
                new SessionId("session-ordering"),
                new[] { first, second, third });

            Assert.Equal(
                new[] { "round-10", "round-2", "round-1" },
                history.Rounds.Select(record => record.RoundId.Value).ToArray());
            Assert.Same(first, history.Rounds[0]);
            Assert.Same(third, history.Rounds[2]);
        }

        [Fact]
        public void HistoryRejectsAdjacentAndNonAdjacentDuplicateRoundIds()
        {
            var first = Record("round-a", "result-a");
            var second = Record("round-b", "result-b");
            var adjacentDuplicate = Record("round-a", "result-c");
            var laterDuplicate = Record("round-a", "result-d");
            var session = new SessionId("session-round-duplicates");

            var adjacent = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new[] { first, adjacentDuplicate }));
            Assert.Equal("rounds", adjacent.ParamName);
            Assert.Contains("duplicate roundId", adjacent.Message);

            var nonAdjacent = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new[] { first, second, laterDuplicate }));
            Assert.Equal("rounds", nonAdjacent.ParamName);
            Assert.Contains("duplicate roundId", nonAdjacent.Message);
            Assert.Contains("rounds[2]", nonAdjacent.Message);
        }

        [Fact]
        public void HistoryRejectsDuplicateResultIdsAcrossRounds()
        {
            var first = Record("round-a", "result-shared");
            var second = Record("round-b", "result-shared");
            var third = Record("round-c", "result-other");
            var session = new SessionId("session-result-duplicates");

            var adjacent = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new[] { first, second }));
            Assert.Equal("rounds", adjacent.ParamName);
            Assert.Contains("duplicate resultId", adjacent.Message);

            var nonAdjacent = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new[] { first, third, Record("round-d", "result-shared") }));
            Assert.Equal("rounds", nonAdjacent.ParamName);
            Assert.Contains("duplicate resultId", nonAdjacent.Message);
        }

        [Fact]
        public void HistoryRejectsRoundAndResultIdentityCollisions()
        {
            var session = new SessionId("session-identity-collisions");

            var roundReusesEarlierResult = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(
                    session,
                    new[] { Record("id-1", "id-2"), Record("id-2", "id-3") }));
            Assert.Equal("rounds", roundReusesEarlierResult.ParamName);
            Assert.Contains("collides with an earlier resultId", roundReusesEarlierResult.Message);

            var sameRecordReusesOwnId = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new[] { Record("id-4", "id-4") }));
            Assert.Equal("rounds", sameRecordReusesOwnId.ParamName);
            Assert.Contains("collides with a roundId", sameRecordReusesOwnId.Message);

            var resultReusesEarlierRound = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(
                    session,
                    new[] { Record("id-5", "id-6"), Record("id-7", "id-5") }));
            Assert.Equal("rounds", resultReusesEarlierRound.ParamName);
            Assert.Contains("collides with a roundId", resultReusesEarlierRound.Message);
        }

        [Fact]
        public void HistoryRejectsNullRoundsListAndNullRecordElements()
        {
            var session = new SessionId("session-null");

            var nullList = Assert.Throws<ArgumentNullException>(() =>
                new PublicSessionHistory(session, null!));
            Assert.Equal("rounds", nullList.ParamName);

            var nullElement = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(session, new PublicRoundRecord[] { Record("round-null", "result-null"), null! }));
            Assert.Equal("rounds", nullElement.ParamName);
            Assert.Contains("null records", nullElement.Message);
        }

        [Fact]
        public void HistoryRejectsDefaultSessionId()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(default(SessionId), Array.Empty<PublicRoundRecord>()));

            Assert.Equal("sessionId", exception.ParamName);
            Assert.Contains("must not be empty", exception.Message);
        }

        [Fact]
        public void HistoryAllowsEmptyOrInProgressHistoryWithoutFinalSummary()
        {
            var empty = new PublicSessionHistory(
                new SessionId("session-empty"),
                Array.Empty<PublicRoundRecord>());
            Assert.Empty(empty.Rounds);
            Assert.Null(empty.FinalSummary);

            var inProgress = new PublicSessionHistory(
                new SessionId("session-in-progress"),
                new[] { Record("round-1", "result-1"), Record("round-2", "result-2") });
            Assert.Equal(2, inProgress.Rounds.Count);
            Assert.Null(inProgress.FinalSummary);
        }

        [Fact]
        public void HistoryRejectsUnfinishedFinalSummary()
        {
            var unfinished = new PublicSessionSummary(
                false,
                "IN_PROGRESS",
                Array.Empty<ScoreView>(),
                Array.Empty<int>());

            var exception = Assert.Throws<ArgumentException>(() =>
                new PublicSessionHistory(
                    new SessionId("session-unfinished"),
                    Array.Empty<PublicRoundRecord>(),
                    unfinished));

            Assert.Equal("finalSummary", exception.ParamName);
            Assert.Contains("must be finished", exception.Message);
        }

        [Fact]
        public void HistoryCopiesRoundListAgainstCallerMutationAndRejectsWrites()
        {
            var first = Record("round-a", "result-a");
            var second = Record("round-b", "result-b");
            var rounds = new List<PublicRoundRecord> { first, second };
            var history = new PublicSessionHistory(new SessionId("session-copy"), rounds);

            rounds.Clear();
            rounds.Add(Record("round-c", "result-c"));

            Assert.Equal(2, history.Rounds.Count);
            Assert.Same(first, history.Rounds[0]);
            Assert.Same(second, history.Rounds[1]);

            var writable = Assert.IsAssignableFrom<IList<PublicRoundRecord>>(history.Rounds);
            Assert.True(writable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() =>
                writable[0] = Record("round-replacement", "result-replacement"));
            Assert.Throws<NotSupportedException>(() => writable.Clear());
            Assert.Throws<NotSupportedException>(() => writable.Add(Record("round-added", "result-added")));
        }

        [Fact]
        public void HistoryCopiesFinalSummaryAndExposesReadOnlyCollections()
        {
            var rankings = new List<int> { 0, 2, 3, 1 };
            var summary = new PublicSessionSummary(
                true,
                "NORMAL_COMPLETION",
                SeatRows(new long[] { 26000, 24000, 25000, 25000 }),
                rankings);
            var history = new PublicSessionHistory(
                new SessionId("session-summary"),
                new[] { Record("round-1", "result-1") },
                summary);

            Assert.NotSame(summary, history.FinalSummary);
            PublicSessionSummary copied = history.FinalSummary!;
            Assert.True(copied.Finished);
            Assert.Equal("NORMAL_COMPLETION", copied.FinishReason);
            Assert.Equal(new long[] { 26000, 24000, 25000, 25000 },
                copied.FinalScores.Select(score => score.Score).ToArray());
            Assert.Equal(new[] { 0, 2, 3, 1 }, copied.Rankings.ToArray());

            rankings[0] = 3;
            Assert.Equal(0, copied.Rankings[0]);

            var writableScores = Assert.IsAssignableFrom<IList<ScoreView>>(copied.FinalScores);
            Assert.True(writableScores.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writableScores[0] = new ScoreView(0, 1));

            var writableRankings = Assert.IsAssignableFrom<IList<int>>(copied.Rankings);
            Assert.True(writableRankings.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writableRankings[0] = 3);
        }

        [Fact]
        public void HistoryReusesExistingSummaryAndAddsNoParallelSessionResult()
        {
            PropertyInfo? summaryProperty = typeof(PublicSessionHistory).GetProperty("FinalSummary");
            Assert.NotNull(summaryProperty);
            Assert.Equal(typeof(PublicSessionSummary), summaryProperty!.PropertyType);

            Assert.Null(typeof(PublicSessionHistory).Assembly.GetType(
                "Mahjong.Contracts.PublicSessionResult",
                throwOnError: false));

            Assert.Equal(
                new[] { "FinalSummary", "Rounds", "SessionId" },
                typeof(PublicSessionHistory)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(
                new[] { "Deltas", "FinalScores", "ResultId", "RoundId" },
                typeof(PublicRoundRecord)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

            PropertyInfo? scoresProperty = typeof(PublicRoundRecord).GetProperty("FinalScores");
            PropertyInfo? deltasProperty = typeof(PublicRoundRecord).GetProperty("Deltas");
            Assert.NotNull(scoresProperty);
            Assert.NotNull(deltasProperty);
            Assert.Equal(typeof(IReadOnlyList<ScoreView>), scoresProperty!.PropertyType);
            Assert.Equal(typeof(IReadOnlyList<ScoreView>), deltasProperty!.PropertyType);
        }

        [Fact]
        public void PublicHistoryGraphContainsNoPrivateDomainOrWireState()
        {
            string[] forbiddenNameFragments =
            {
                "cheat", "seed", "tile", "hand", "wall", "ura", "dora",
                "ledger", "account", "matchsession", "private"
            };
            var visited = new HashSet<Type>();

            foreach (Type root in new[] { typeof(PublicRoundRecord), typeof(PublicSessionHistory) })
            {
                foreach (PropertyInfo property in root.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    AssertNameAllowed(property.Name, forbiddenNameFragments);
                    AssertTypeAllowed(property.PropertyType);
                    ScanType(property.PropertyType, visited, forbiddenNameFragments);
                }

                foreach (FieldInfo field in root.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    AssertNameAllowed(field.Name, forbiddenNameFragments);
                    AssertTypeAllowed(field.FieldType);
                    ScanType(field.FieldType, visited, forbiddenNameFragments);
                }
            }
        }

        private static void ScanType(Type type, HashSet<Type> visited, string[] forbiddenNameFragments)
        {
            if (!visited.Add(type))
            {
                return;
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                return;
            }

            if (type.IsArray)
            {
                Type element = type.GetElementType()!;
                AssertTypeAllowed(element);
                ScanType(element, visited, forbiddenNameFragments);
                return;
            }

            if (type.IsGenericType)
            {
                AssertTypeAllowed(type.GetGenericTypeDefinition());
                foreach (Type argument in type.GetGenericArguments())
                {
                    AssertTypeAllowed(argument);
                    ScanType(argument, visited, forbiddenNameFragments);
                }

                return;
            }

            AssertTypeAllowed(type);
            if (type.Namespace != "Mahjong.Contracts")
            {
                return;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                AssertNameAllowed(property.Name, forbiddenNameFragments);
                AssertTypeAllowed(property.PropertyType);
                ScanType(property.PropertyType, visited, forbiddenNameFragments);
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                AssertNameAllowed(field.Name, forbiddenNameFragments);
                AssertTypeAllowed(field.FieldType);
                ScanType(field.FieldType, visited, forbiddenNameFragments);
            }
        }

        private static void AssertTypeAllowed(Type type)
        {
            string? ns = type.Namespace;
            bool allowed = ns == "System"
                || ns == "System.Collections.Generic"
                || ns == "System.Collections.ObjectModel"
                || ns == "Mahjong.Contracts";
            Assert.True(
                allowed,
                "Public history graph exposed a non-contract type: " + type.FullName + " (" + ns + ").");
        }

        private static void AssertNameAllowed(string name, string[] forbiddenNameFragments)
        {
            string lowered = name.ToLowerInvariant();
            foreach (string fragment in forbiddenNameFragments)
            {
                Assert.False(
                    lowered.Contains(fragment),
                    "Public history graph exposed forbidden name fragment '" + fragment + "' in '" + name + "'.");
            }
        }

        private static PublicRoundRecord Record(
            string roundId,
            string resultId,
            long[]? scores = null,
            long[]? deltas = null)
        {
            return new PublicRoundRecord(
                new RoundId(roundId),
                resultId,
                SeatRows(scores ?? DefaultScores),
                SeatRows(deltas ?? DefaultDeltas));
        }

        private static ScoreView[] SeatRows(long[] values)
        {
            if (values.Length != 4)
            {
                throw new ArgumentException("test helper requires four values.", nameof(values));
            }

            return new[]
            {
                new ScoreView(0, values[0]),
                new ScoreView(1, values[1]),
                new ScoreView(2, values[2]),
                new ScoreView(3, values[3])
            };
        }

        private static ScoreView[] RowsCyclingSeats(int count)
        {
            var rows = new ScoreView[count];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new ScoreView(i % 4, i);
            }

            return rows;
        }
    }
}
