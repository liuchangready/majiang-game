using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round3ReviewProbes
    {
        [Theory]
        [InlineData("duplicate")]
        [InlineData("wrong-type")]
        [InlineData("missing-fields")]
        public async Task InvalidPersistedEventFactsMustPreventServing(string corruption)
        {
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            await using DurableHarness h = await DurableHarness.CreateAsync(projectionSource: source);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(
                    P31Fixtures.DiscardCurrent(h.InitialRound), 0, "facts-corruption"))).Status);
            TableCommittedFactsSnapshot stored = (await h.Store.LoadCommittedFactsAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            TableProjectionContextData good = TableProjectionFactsCodec.Decode(
                stored.Payload, h.InitialRound.RoundId, new StateVersion(1));
            Assert.True(good.EventFacts.Count >= 2);
            TableProjectionEventFact[] facts = good.EventFacts.ToArray();
            if (corruption == "duplicate")
            {
                // Count stays correct, but one committed event has no fact at all.
                facts[1] = facts[0];
            }
            else
            {
                TableProjectionEventFact first = facts[0];
                facts[0] = new TableProjectionEventFact(
                    corruption == "wrong-type" ? facts[1].Type : first.Type,
                    first.EventSeq,
                    first.CommandVersion,
                    first.EventOrdinal,
                    corruption == "missing-fields" ? new TableProjectionEventFacts() : first.Facts);
                if (corruption == "wrong-type")
                {
                    Assert.NotEqual(first.Type, facts[0].Type);
                }
            }

            var invalid = new TableProjectionContextData(
                good.RoundId, good.StateVersion, good.SessionVersion, good.Cheat,
                good.PublicFacts, good.PrivateFacts, good.Session, good.Policy,
                facts, good.ConcealedMeldFacts);
            byte[] payload = TableProjectionFactsCodec.Encode(invalid);
            h.Store.SetCommittedFactsForTest(
                DurableHarness.Room, payload, TablePayloadSha256.Compute(payload));

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.False(recovered.Recovered,
                corruption + " event facts reached Serving: " + recovered.ReasonCode + ": " + recovered.Detail);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }
    }
}
