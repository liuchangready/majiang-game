using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round4ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DomainContradictingEventFactsMustPreventServing(bool terminal)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness h = await DurableHarness.CreateAsync(
                projectionSource: terminal ? DurableHarness.TerminalFactsSource() : null,
                recoverySink: sink);
            RoundStateMachine round = h.InitialRound;
            RoundCommand command = terminal
                ? RoundCommand.Tsumo(round.RoundId, 0, new RoundSettlement(
                    "semantic-terminal", RoundOutcome.Tsumo(0),
                    new long[] { 26000, 24000, 25000, 25000 }, 0,
                    new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)), 1234L))
                : P31Fixtures.DiscardCurrent(round);
            Assert.Equal(TableOperationStatus.Accepted, (await h.SubmitAsync(
                h.Envelope(command, 0, "semantic-facts", api: terminal ? "TSUMO" : "DISCARD"))).Status);
            TableCommittedFactsSnapshot stored = (await h.Store.LoadCommittedFactsAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            TableProjectionContextData good = TableProjectionFactsCodec.Decode(
                stored.Payload, round.RoundId, new StateVersion(1));
            TableProjectionEventFact[] facts = good.EventFacts.ToArray();
            int index = Array.FindIndex(facts, value => value.Type ==
                (terminal ? ProtocolEventType.RoundEnded : ProtocolEventType.TileDrawn));
            Assert.True(index >= 0);
            TableProjectionEventFact original = facts[index];
            TableProjectionEventFacts invalidFields;
            if (terminal)
            {
                // Valid wire values, but the immutable round specification says otherwise.
                Assert.Equal((uint)round.RoundWind, original.Facts.RoundWind);
                invalidFields = new TableProjectionEventFacts(
                    turnNumber: original.Facts.TurnNumber,
                    roundWind: ((uint)round.RoundWind + 1U) % 4U,
                    roundNumber: original.Facts.RoundNumber);
            }
            else
            {
                // A single discard/draw has not exhausted this wall.
                Assert.True(original.Facts.WallRemaining > 0U);
                invalidFields = new TableProjectionEventFacts(
                    turnNumber: original.Facts.TurnNumber, wallRemaining: 0U);
            }

            facts[index] = new TableProjectionEventFact(
                original.Type, original.EventSeq, original.CommandVersion,
                original.EventOrdinal, invalidFields);
            byte[] payload = TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                good.RoundId, good.StateVersion, good.SessionVersion, good.Cheat,
                good.PublicFacts, good.PrivateFacts, good.Session, good.Policy,
                facts, good.ConcealedMeldFacts));
            h.Store.SetCommittedFactsForTest(
                DurableHarness.Room, payload, TablePayloadSha256.Compute(payload));

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.False(result.Recovered,
                (terminal ? "Wrong round wind" : "Wrong wall remaining") +
                " reached Serving: " + result.ReasonCode + ": " + result.Detail);
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }
    }
}
