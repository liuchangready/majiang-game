using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    public sealed class CodexP41aReviewTests
    {
        private static RoundStateMachine Build(Dictionary<int, int> assigned, int dealer = 0)
        {
            var used = new HashSet<int>(assigned.Values);
            Assert.Equal(assigned.Count, used.Count);
            var rest = new Queue<int>(Enumerable.Range(0, 136).Where(id => !used.Contains(id)));
            var wall = Enumerable.Range(0, 136).Select(i => new TileId(assigned.TryGetValue(i, out int id) ? id : rest.Dequeue())).ToArray();
            var tiles = TilePositionMap.CreateDealt(RecordedRandomDraw.Create("review-wall", wall), dealer);
            return RoundStateMachine.CreateDealt(new RoundSpec("review-round", dealer, 1, 1, 0,
                new long[] { 25000, 25000, 25000, 25000 }, 0), tiles, RoundPhase.CheatWindow);
        }

        [Fact]
        public void NoYakuRonIsNotALegalCandidate()
        {
            // 123m 456p 789s 234p EE, waiting on 5p; honor pair prevents pinfu.
            int[] hand = { 0, 4, 8, 48, 56, 96, 100, 104, 40, 44, 49, 108, 109 };
            int[] slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
            var map = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++) map[slots[i]] = hand[i];
            map[52] = 52;
            RoundStateMachine round = Build(map, 1);
            RoundTransition discarded = round.Execute(RoundCommand.Discard(round.RoundId, 1, new TileId(52), new[] { 0 }));
            Assert.True(discarded.IsAccepted, discarded.Error?.ToString());
            round = discarded.State!;
            Assert.NotNull(round.Window);
            var closed = round.Tiles.TilesInZone(TileZone.Hand, 0);
            Assert.Equal(13, closed.Count);
            Assert.True(HandDecomposer.CanWin(closed.Concat(new[] { new TileId(52) })));
            Assert.Null(WinningScoreEvaluator.Evaluate(new PlayerHand(closed),
                new WinContext(new TileId(52), false, true, 4, 1)));
            Assert.False(LocalCandidateProvider.CanRon(round, 0, closed, new TileId(52), new bool[4], new bool[4]));
        }

        [Fact]
        public void RiichiAfterClosedKanRemainsAvailable()
        {
            int[] hand = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var map = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++) map[slots[i]] = hand[i];
            map[122] = 132;
            RoundStateMachine round = Build(map);
            RoundTransition kan = round.Execute(RoundCommand.ClosedKan(round.RoundId, 0, new TileId(0), new TileId(1), new TileId(2), new TileId(3)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            round = kan.State!;
            if (round.Phase == RoundPhase.KanDraw)
            {
                RoundTransition draw = round.Execute(RoundCommand.Draw(round.RoundId, 0));
                Assert.True(draw.IsAccepted, draw.Error?.ToString());
                round = draw.State!;
            }
            RoundPreparation riichi = round.Prepare(RoundCommand.Riichi(round.RoundId, 0, new TileId(132), null));
            Assert.True(riichi.IsReady, riichi.Error?.ToString());
            Assert.Contains(LocalCandidateProvider.DeriveTurnCandidates(round, 0, new bool[4], new bool[4]),
                action => action.Kind == LegalActionKind.Riichi && action.Tile?.Value == 132);
        }

        [Fact]
        public async Task ChiCandidateCarriesTheHandEntities()
        {
            await using var harness = new P41Harness();
            int[] hand = { 0, 4, 12, 16, 20, 24, 28, 36, 40, 44, 48, 52, 56, 132 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var map = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++) map[slots[i]] = hand[i];
            map[53] = 133; map[54] = 134; map[55] = 8;
            map[100] = 9; map[101] = 10; map[102] = 11;
            harness.Options.InitialRound = Build(map);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Type accepted = typeof(LocalHostCandidateAcceptanceTests);
            var opened = (Task<(ITableChannel, TableSession)>)accepted.GetMethod("OpenAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { harness, cts.Token })!;
            (ITableChannel channel, TableSession session) = await opened;
            Task pump = (Task)accepted.GetMethod("StartPump", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { session, cts.Token })!;
            try
            {
                var wait = accepted.GetMethod("WaitForDecisionAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
                PlayerSnapshot initial = await (Task<PlayerSnapshot>)wait.Invoke(null, new object[] { session, cts.Token })!;
                var d = initial.PrivateView.Decision!;
                var payload = CommandPayloadFactory.SubmitAction(initial.RoundId, d.DecisionId, d.ControlEpoch,
                    ProtocolActionType.DiscardTile, tileIds: new uint[] { 132 });
                await session.SubmitAsync(new ProtocolCommand(CommandKind.SubmitAction, payload, new RequestId("review-chi-discard"),
                    session.Binding!.Scope.ScopeId, new StateVersion(initial.StateVersion), new RoundId(initial.RoundId),
                    decisionId: new DecisionId(d.DecisionId), controlEpoch: new ControlEpoch(d.ControlEpoch)), cts.Token);
                var response = accepted.GetMethod("WaitForActionTypeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
                PlayerSnapshot snapshot = await (Task<PlayerSnapshot>)response.Invoke(null,
                    new object[] { session, ProtocolActionType.DeclareChi, cts.Token })!;
                var chi = snapshot.PrivateView.Decision!.Actions.First(a => a.ActionType == (uint)ProtocolActionType.DeclareChi);
                Assert.Contains(0u, chi.TileIds);
                Assert.Contains(4u, chi.TileIds);
            }
            finally
            {
                cts.Cancel();
                await pump;
                await session.CloseAsync("REVIEW_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("REVIEW_DONE", true), CancellationToken.None);
            }
        }
    }
}
