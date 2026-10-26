using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round18ReviewProbes
    {
        [Theory]
        [InlineData(250L)]
        [InlineData(20000L)]
        public async Task SameBootCheckpointCannotRestartGraceByShiftingBothTimestamps(long shift)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            Assert.True((await source.Actor.DisconnectAsync(
                0, new OwnerEpoch(1), new ControlEpoch(0), "review", ct)).IsChanged);
            TableCheckpoint good = await source.SaveCheckpointAsync();
            TableStateRow before = (await source.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            int commits = source.Store.CommitCount;
            TableControlCheckpoint original = TableControlCheckpointCodec.Decode(good.ControlPayload);
            SeatControlState[] seats = original.Snapshot.Seats.ToArray();
            SeatControlState seat = seats[0];
            Assert.True(seat.GraceDeadlineMillis.HasValue);
            source.Clock.Now += shift;
            seats[0] = new SeatControlState(seat.Seat, seat.Status, seat.Kind, seat.ControlEpoch,
                seat.DisconnectedAtMillis!.Value + shift, seat.GraceDeadlineMillis!.Value + shift,
                seat.AiProfileId);
            byte[] control = TableControlCheckpointCodec.Encode(new TableControlCheckpoint(
                new ControlRegistrySnapshot(original.Snapshot.Policy, seats, original.Snapshot.PreferredHumanKinds),
                original.ControlVersion));
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq, good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, control, TablePayloadSha256.Compute(control),
                good.TimingPayload, good.TimingPayloadSha256, good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                before.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);

            TableDurableCommitResult saved = await source.Store.SaveCheckpointAsync(candidate,
                new TableCheckpointFence(DurableHarness.Room, DurableHarness.Owner, new OwnerEpoch(1),
                    before.StateVersion, before.ControlVersion, before.TimingVersion), ct);

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "A same-boot checkpoint restarted grace without recovery/control commit: shift=" + shift +
                "; saved=" + saved.Disposition + "; oldDeadline=" + seat.GraceDeadlineMillis +
                "; newDeadline=" + seats[0].GraceDeadlineMillis + "; now=" + source.Clock.Now);
            Assert.Equal(commits, source.Store.CommitCount);
            Assert.Equal(before.TimingVersion,
                (await source.Store.LoadStateAsync(DurableHarness.Room, ct))!.TimingVersion);
            Assert.Equal(good.ControlPayloadSha256,
                (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!.ControlPayloadSha256);
        }
    }
}
