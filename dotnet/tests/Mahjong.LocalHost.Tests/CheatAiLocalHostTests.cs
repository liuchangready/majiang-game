using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Domain;
using Mahjong.LocalHost;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class CheatAiLocalHostTests
    {
        [Fact]
        public async Task RandomCheatBasicAiCompletesARealRoundWithConservedTilesAndCheatLedger()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(
                P41Harness.FindRepositoryRoot(),
                "build",
                "cheat-ai-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var environment = new LocalHostEnvironment
                {
                    NextInt = bound => (int)(0x5DEECE66DUL % (ulong)bound),
                };
                var options = new LocalTableHostOptions
                {
                    SaveDirectory = root,
                    RoomId = "cheat-ai-round",
                    RulesetVersion = 1,
                    OwnerInstanceId = "cheat-ai-test",
                    LeaseDuration = TimeSpan.FromMinutes(5),
                    Environment = environment,
                    SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>()),
                    Ai = new LocalAiOptions
                    {
                        ProfileId = LocalAiProfiles.RandomCheatBasic,
                        Seed = 12345UL,
                        AllowCheats = true,
                        MaxSpendPerRound = 60,
                    },
                };

                await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(options, ct))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    LocalRoundSummary summary;
                    do
                    {
                        await Task.Delay(25, timeout.Token);
                        summary = await LocalTableHost.GetRoundSummaryAsync(root, timeout.Token);
                    }
                    while (!summary.Finished);

                    Assert.True(summary.WallConserved, string.Join("; ", summary.ConservationErrors));
                    Assert.Equal(136, summary.WallAssignedCount);
                    Assert.Equal(4, summary.FinalScores.Count);
                    Assert.Equal(
                        100_000L,
                        summary.FinalScores.Sum() + summary.RemainingRiichiSticks * 1000L);
                    Assert.True(
                        summary.CheatUsage.Sum(usage => usage.Count) > 0,
                        "the seeded cheat AI did not use any cheat in this round");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
