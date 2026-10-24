using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round16ReviewProbes
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task SameControlVersionMustNotAllowSeatEpochRewrite(bool acceptedCommit, bool rewriteEpoch)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            TableCheckpoint initial = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            if (acceptedCommit)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await source.SubmitAsync(source.Envelope(
                        P31Fixtures.DiscardCurrent(source.InitialRound), 0, "round16-discard"))).Status);
            }

            TableCheckpoint good = await source.SaveCheckpointAsync();
            await using DurableHarness target = await DurableHarness.CreateAsync(initialCheckpoint: initial);
            TableStateRow before = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            TableControlCheckpoint original = TableControlCheckpointCodec.Decode(good.ControlPayload);
            SeatControlState[] seats = original.Snapshot.Seats.ToArray();
            SeatControlState seat = seats[0];
            if (rewriteEpoch)
            {
                seats[0] = new SeatControlState(seat.Seat, seat.Status, seat.Kind,
                    seat.ControlEpoch + 10, seat.DisconnectedAtMillis, seat.GraceDeadlineMillis, seat.AiProfileId);
            }

            byte[] control = TableControlCheckpointCodec.Encode(new TableControlCheckpoint(
                new ControlRegistrySnapshot(original.Snapshot.Policy, seats, original.Snapshot.PreferredHumanKinds),
                original.ControlVersion));
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, control, TablePayloadSha256.Compute(control),
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                before.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);
            int commits = target.Store.CommitCount;
            TableDurableCommitResult saved;
            if (acceptedCommit)
            {
                var events = await source.Store.LoadEventsAfterAsync(DurableHarness.Room, new EventSeq(0), ct);
                var commands = await source.Store.LoadCommandsAfterAsync(DurableHarness.Room, new StateVersion(0), ct);
                saved = await target.Store.CommitAsync(new TableDurableCommitRequest(
                    DurableHarness.Room, DurableHarness.Owner, new OwnerEpoch(1), before.StateVersion,
                    null, true, events, commands[0], null, candidate,
                    good.StatePayload, good.StateFingerprint, false,
                    TableTimingCheckpointCodec.Decode(good.TimingPayload), before.TimingVersion + 1,
                    good.CommittedFactsPayload, good.CommittedFactsPayloadSha256), ct);
            }
            else
            {
                saved = await target.Store.SaveCheckpointAsync(candidate,
                    new TableCheckpointFence(DurableHarness.Room, DurableHarness.Owner,
                        new OwnerEpoch(1), before.StateVersion, before.ControlVersion, before.TimingVersion), ct);
            }

            TableStateRow after = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            int afterCommits = target.Store.CommitCount;
            TableRecoveryResult recovered = await target.Actor.RecoverFromStoreAsync(ct);
            if (!rewriteEpoch)
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                Assert.Equal(seat.ControlEpoch, recovered.Baseline!.Control!.State.Seats[0].ControlEpoch);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Unjournaled seat epoch rewrite at unchanged controlVersion: acceptedCommit=" + acceptedCommit +
                "; saved=" + saved.Disposition + "; recovered=" + recovered.Recovered +
                "; recoveredEpoch=" + recovered.Baseline?.Control?.State.Seats[0].ControlEpoch +
                "; originalEpoch=" + seat.ControlEpoch + "; reason=" + recovered.ReasonCode);
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.ControlVersion, after.ControlVersion);
            Assert.Equal(before.TimingVersion, after.TimingVersion);
            Assert.Equal(commits, afterCommits);
            Assert.Equal(0, target.Store.EventCount);
            Assert.True(recovered.Recovered, "Rejected rewrite must preserve the original recovery point.");
            Assert.Equal(seat.ControlEpoch, recovered.Baseline!.Control!.State.Seats[0].ControlEpoch);
        }
    }
}
