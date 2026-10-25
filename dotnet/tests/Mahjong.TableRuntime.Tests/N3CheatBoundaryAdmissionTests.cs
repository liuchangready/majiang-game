using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// N3 residual note obligation tm-decision-mismatch (V4 roundIdGiven=null: authority
    /// has no active round).  The accepted actor always owns a concrete RoundStateMachine,
    /// so the exact V4 input has no safe production entry.  These tests record the
    /// construction rejection and a reachable, clearly-labelled different rejection; they
    /// do not add a null-round port or claim the no-open-decision case is the source case.
    /// </summary>
    public class N3CheatBoundaryAdmissionTests
    {
        [Fact]
        public void NullRoundIsRejectedAtCommandConstructionAndActorConstruction()
        {
            TileId[] tiles =
            {
                new TileId(0), new TileId(4), new TileId(8), new TileId(12)
            };
            var payload = RoundCheatPayload.TileMark("g-null-round", tiles);

            ArgumentException nullRound = Assert.Throws<ArgumentException>(
                () => new RoundCheatCommand(
                    null!,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "req-null-round",
                    payload));
            Assert.Equal("roundId", nullRound.ParamName);

            ArgumentException blankRound = Assert.Throws<ArgumentException>(
                () => new RoundCheatCommand(
                    "   ",
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "req-blank-round",
                    payload));
            Assert.Equal("roundId", blankRound.ParamName);

            // The actor constructor is the only place that could own "no active round";
            // it requires initialRound and rejects null before any commit/publication port
            // is touched.  Passing null ports is safe because the guard fires first.
            var options = new TableActorOptions
            {
                RoomId = "room-null-round",
                MailboxCapacity = 8,
                ControlReserve = 8
            };
            ArgumentNullException noActorRound = Assert.Throws<ArgumentNullException>(
                () => new TableActor(options, null!, null!, null!));
            Assert.Equal("initialRound", noActorRound.ParamName);
        }

        [Fact]
        public async Task NoOpenDecisionRejectionIsTypedAndSideEffectFree()
        {
            // NOT the V4 tm-decision-mismatch source case.  The actor has a concrete
            // round but no open pending decision; this exercises the reachable
            // DecisionMismatch branch and proves the detail differs from the null-round
            // construction rejection above.
            RoundStateMachine round = P31Fixtures.Dealt("round-n3-decision");
            TableActor actor = CreateCheatActor(round);
            await using (actor)
            {
                TableSnapshot before = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0UL, before.StateVersion.Value);
                Assert.Empty(before.RecentEvents);

                TileId[] hand = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
                string requestId = "req-no-open-decision";
                byte[] payload = Encoding.UTF8.GetBytes(
                    "TM|" + requestId + "|" + round.RoundId + "|" + string.Join(",", hand.Select(t => t.Value)));
                var envelope = new TableCheatCommandEnvelope(
                    new RoundCheatCommand(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitTileMark,
                        requestId,
                        RoundCheatPayload.TileMark("g-no-open-decision", hand)),
                    new RequestId(requestId),
                    new StateVersion(0UL),
                    new OwnerEpoch(1UL),
                    new ControlEpoch(0UL),
                    new DecisionId("not-open-decision"),
                    0L,
                    new RequestScopeId("scope-n3-decision"),
                    "CHEAT",
                    TablePayloadSha256.Compute(payload));

                TableOperationResult result = await actor.SubmitCheatAsync(
                    envelope, TestContext.Current.CancellationToken);

                Assert.Equal(TableOperationStatus.Rejected, result.Status);
                Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, result.ReasonCode);
                Assert.Contains("not open for this seat", result.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("does not match", result.Detail, StringComparison.Ordinal);
                Assert.Empty(result.Events);
                Assert.Null(result.CommandVersion);

                TableSnapshot after = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
                Assert.Equal(before.StateVersion, after.StateVersion);
                Assert.Equal(before.LastEventSeq, after.LastEventSeq);
                Assert.Equal(before.StateFingerprint, after.StateFingerprint);
                Assert.Empty(after.RecentEvents);
            }
        }

        private static TableActor CreateCheatActor(RoundStateMachine round)
        {
            var clock = new ManualTableClock { Now = 1_000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = 10,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            var economy = new CheatEconomyConfig(true, 40, costs);
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                CheatState.Initialize(economy), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-n3-decision",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(10_000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = economy,
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            return new TableActor(options, round, commitPort, clock, sink);
        }
    }
}
