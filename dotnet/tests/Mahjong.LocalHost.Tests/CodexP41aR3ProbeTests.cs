using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class CodexP41aR3ProbeTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CallWithoutDrawMustNotOfferTsumo(bool chi)
        {
            int dealer = chi ? 3 : 1;
            int target = chi ? 8 : 126;
            int[] hand = chi
                ? new[] { 0, 4, 12, 16, 20, 24, 28, 32, 124, 125, 126, 108, 109 }
                : new[] { 124, 125, 0, 4, 8, 12, 16, 20, 24, 28, 32, 108, 109 };
            int[] slots = chi
                ? new[] { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 }
                : new[] { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
            var assigned = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++) assigned[slots[i]] = hand[i];
            assigned[52] = target;
            var used = new HashSet<int>(assigned.Values);
            var rest = new Queue<int>(Enumerable.Range(0, 136).Where(t => !used.Contains(t)));
            var wall = Enumerable.Range(0, 136).Select(i => new TileId(assigned.TryGetValue(i, out int t) ? t : rest.Dequeue())).ToArray();
            var tiles = TilePositionMap.CreateDealt(RecordedRandomDraw.Create("r3-wall", wall), dealer);
            var round = RoundStateMachine.CreateDealt(new RoundSpec("r3-round", dealer, 1, 1, 0,
                new long[] { 25000, 25000, 25000, 25000 }, 0), tiles, RoundPhase.CheatWindow);
            var discard = round.Execute(RoundCommand.Discard(round.RoundId, dealer, new TileId(target), new[] { 0 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            round = discard.State!;
            var call = round.Execute(chi
                ? RoundCommand.Chi(round.RoundId, 0, new TileId(target), new TileId(0), new TileId(4))
                : RoundCommand.Pon(round.RoundId, 0, new TileId(target), new TileId(124), new TileId(125)));
            Assert.True(call.IsAccepted, call.Error?.ToString());
            round = call.State!;
            Assert.Equal(RoundPhase.Discard, round.Phase);
            Assert.False(round.Tiles.DrawnTile.HasValue);
            var tsumo = round.Prepare(RoundCommand.Tsumo(round.RoundId, 0, null!));
            Assert.False(tsumo.IsReady);
            Assert.Equal(RoundTransitionErrorCode.NoDrawnTile, tsumo.Error!.Code);
            var options = LocalCandidateProvider.DeriveTurnCandidates(round, 0, new bool[4], new bool[4]);
            Assert.Contains(options, option => option.Kind == LegalActionKind.Discard);
            Assert.DoesNotContain(options, option => option.Kind == LegalActionKind.Tsumo);
        }
    }
}
