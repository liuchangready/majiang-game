using Mahjong.Domain;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalRecordedRoundsTests
    {
        [Fact]
        public void replay_projection_keeps_draw_timing_but_omits_private_tile_and_detail()
        {
            var draw = new TableEventRecord(new EventSeq(1), new StateVersion(1), 0,
                RoundEventType.TileDrawn, 0, new TileId(17), "private draw detail");
            var discard = new TableEventRecord(new EventSeq(2), new StateVersion(2), 0,
                RoundEventType.TileDiscarded, 0, new TileId(18), "internal detail");

            LocalRecordedEvent projectedDraw = LocalRecordedRounds.ProjectEvent(draw)!;
            Assert.Equal("TileDrawn", projectedDraw.TypeCode);
            Assert.Null(projectedDraw.PublicTile);
            LocalRecordedEvent projected = LocalRecordedRounds.ProjectEvent(discard)!;
            Assert.Equal("TileDiscarded", projected.TypeCode);
            Assert.Equal(0, projected.Seat);
            Assert.Equal((int?)18, projected.PublicTile);
            Assert.Equal((ulong)2, projected.Sequence);
        }
    }
}
