using System;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public class ControlRegistryTests
    {
        [Fact]
        public void DefaultSeatsMatchV4InitialControllerRegistry()
        {
            var registry = new ControlRegistry();

            Assert.True(registry.IsHuman(0));
            Assert.Equal(SeatControllerKind.Computer, registry.GetController(1));
            Assert.Equal(SeatControllerKind.Computer, registry.GetController(2));
            Assert.Equal(SeatControllerKind.Computer, registry.GetController(3));
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(0L, registry.GetEpoch(seat));
            }
        }

        [Fact]
        public void EveryControllerChangeIncrementsThatSeatEpoch()
        {
            var registry = new ControlRegistry();

            registry.SetController(0, SeatControllerKind.Computer);
            Assert.Equal(1L, registry.GetEpoch(0));
            Assert.False(registry.IsHuman(0));

            registry.SetController(0, SeatControllerKind.RemoteHuman);
            Assert.Equal(2L, registry.GetEpoch(0));
            Assert.True(registry.IsHuman(0));

            registry.SetController(0, SeatControllerKind.RemoteHuman);
            Assert.Equal(3L, registry.GetEpoch(0));
        }

        [Fact]
        public void InitializeIncrementsEverySeatEpoch()
        {
            var registry = new ControlRegistry();
            registry.SetController(2, SeatControllerKind.LocalHuman);

            registry.Initialize(new[]
            {
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.Computer,
                SeatControllerKind.Computer,
                SeatControllerKind.Takeover
            });

            Assert.Equal(1L, registry.GetEpoch(0));
            Assert.Equal(1L, registry.GetEpoch(1));
            Assert.Equal(2L, registry.GetEpoch(2));
            Assert.Equal(1L, registry.GetEpoch(3));
            Assert.Equal(SeatControllerKind.Takeover, registry.GetController(3));
        }

        [Fact]
        public void ImmediateAiDisconnectTakesOverAndIsIdempotent()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.ImmediateAi));

            SeatControlState state = registry.Disconnect(0, 1000L);

            Assert.True(state.IsDisconnected);
            Assert.Equal(SeatControllerKind.Takeover, state.Kind);
            Assert.Equal(1L, state.ControlEpoch);
            Assert.Equal(1000L, state.DisconnectedAtMillis);
            Assert.Null(state.GraceDeadlineMillis);

            SeatControlState again = registry.Disconnect(0, 9000L);
            Assert.Same(state, again);
            Assert.Equal(1000L, again.DisconnectedAtMillis);
            Assert.Equal(1L, again.ControlEpoch);
        }

        [Fact]
        public void GraceThenAiTakesOverExactlyAtDeadline()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.GraceThenAi, 5000L));

            SeatControlState disconnected = registry.Disconnect(0, 2000L);
            Assert.Equal(7000L, disconnected.GraceDeadlineMillis);
            Assert.True(disconnected.IsHuman);
            Assert.Equal(0L, disconnected.ControlEpoch);

            Assert.False(registry.CheckGraceExpiration(0, 6999L));
            Assert.True(registry.IsHuman(0));
            Assert.Equal(0L, registry.GetEpoch(0));

            Assert.True(registry.CheckGraceExpiration(0, 7000L));
            Assert.True(registry.GetState(0).IsTakenOver);
            Assert.Equal(1L, registry.GetEpoch(0));
            Assert.Null(registry.GetState(0).GraceDeadlineMillis);

            Assert.False(registry.CheckGraceExpiration(0, 9000L));
        }

        [Fact]
        public void ReconnectAloneDoesNotReclaimAndExplicitReclaimBumpsEpoch()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.ImmediateAi));
            registry.Disconnect(0, 1000L);
            long takeoverEpoch = registry.GetEpoch(0);

            SeatControlState reconnected = registry.Reconnect(0, 2000L);
            Assert.True(reconnected.IsConnected);
            Assert.Equal(SeatControllerKind.Takeover, reconnected.Kind);
            Assert.Equal(takeoverEpoch, reconnected.ControlEpoch);
            Assert.False(registry.ValidateEpoch(0, 0L).IsValid);

            SeatControlState reclaimed = registry.ReclaimControl(0);
            Assert.Equal(SeatControllerKind.LocalHuman, reclaimed.Kind);
            Assert.Equal(takeoverEpoch + 1L, reclaimed.ControlEpoch);
            Assert.False(registry.ValidateEpoch(0, takeoverEpoch).IsValid);
            Assert.True(registry.ValidateEpoch(0, reclaimed.ControlEpoch).IsValid);
        }

        [Fact]
        public void ReconnectWithReclaimFlagAlsoBumpsEpochAndClearsProfile()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.ImmediateAi, 0L, "ai-x"));
            registry.Disconnect(0, 1000L);
            long takeoverEpoch = registry.GetEpoch(0);

            SeatControlState reclaimed = registry.Reconnect(0, 2000L, reclaim: true);

            Assert.Equal(SeatControllerKind.LocalHuman, reclaimed.Kind);
            Assert.Equal(takeoverEpoch + 1L, reclaimed.ControlEpoch);
            Assert.Null(reclaimed.AiProfileId);
            Assert.Null(reclaimed.GraceDeadlineMillis);
        }

        [Fact]
        public void RemoteHumanSeatReclaimsAsRemoteHuman()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.ImmediateAi));
            registry.SetController(0, SeatControllerKind.RemoteHuman);
            registry.Disconnect(0, 1000L);

            SeatControlState reclaimed = registry.ReclaimControl(0);

            Assert.Equal(SeatControllerKind.RemoteHuman, reclaimed.Kind);
            Assert.Equal(3L, reclaimed.ControlEpoch);
        }

        [Fact]
        public void StaleGraceTimerCannotTakeOverANewerController()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.GraceThenAi, 5000L));
            registry.Disconnect(0, 2000L);
            Assert.True(registry.CheckGraceExpiration(0, 7000L, 0L));
            long takeoverEpoch = registry.GetEpoch(0);
            Assert.Equal(1L, takeoverEpoch);

            registry.ReclaimControl(0);
            long reclaimedEpoch = registry.GetEpoch(0);
            Assert.Equal(takeoverEpoch + 1L, reclaimedEpoch);

            Assert.False(registry.CheckGraceExpiration(0, 9000L, takeoverEpoch));
            Assert.True(registry.IsHuman(0));
            Assert.Equal(reclaimedEpoch, registry.GetEpoch(0));
        }

        [Fact]
        public void TimeoutOnlyNeverTakesOver()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.TimeoutOnly));

            SeatControlState state = registry.Disconnect(0, 1000L);
            Assert.Null(state.GraceDeadlineMillis);
            Assert.False(registry.CheckGraceExpiration(0, 100000L));
            Assert.True(registry.IsHuman(0));
            Assert.Equal(0L, registry.GetEpoch(0));
        }

        [Fact]
        public void GraceModeDoesNotApplyToAlreadyAutomatedSeat()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.GraceThenAi, 5000L));

            SeatControlState state = registry.Disconnect(1, 1000L);

            Assert.True(state.IsDisconnected);
            Assert.True(state.IsAutomated);
            Assert.Null(state.GraceDeadlineMillis);
            Assert.Equal(0L, state.ControlEpoch);
        }

        [Fact]
        public void ExplicitTakeoverBumpsEpochAndIsIdempotent()
        {
            var registry = new ControlRegistry();

            SeatControlState first = registry.RequestTakeover(0);
            Assert.Equal(SeatControllerKind.Takeover, first.Kind);
            Assert.Equal(1L, first.ControlEpoch);

            SeatControlState second = registry.RequestTakeover(0);
            Assert.Same(first, second);
            Assert.Equal(1L, registry.GetEpoch(0));
        }

        [Fact]
        public void AutomatedActionRequiresAutomatedSeatAndCurrentEpoch()
        {
            var registry = new ControlRegistry();

            ControlValidation human = registry.ValidateAutomatedAction(0, 0L);
            Assert.False(human.IsValid);
            Assert.Equal(ControlValidationCode.NotAutomated, human.Code);

            ControlValidation ai = registry.ValidateAutomatedAction(1, 0L);
            Assert.True(ai.IsValid);

            Assert.False(registry.ValidateAutomatedAction(1, 1L).IsValid);

            registry.SetController(1, SeatControllerKind.Takeover);
            Assert.True(registry.ValidateAutomatedAction(1, 1L).IsValid);
            Assert.False(registry.ValidateAutomatedAction(1, 0L).IsValid);
        }

        [Fact]
        public void HumanActionRequiresHumanSeat()
        {
            var registry = new ControlRegistry();

            Assert.True(registry.ValidateHumanAction(0, 0L).IsValid);
            ControlValidation ai = registry.ValidateHumanAction(1, 0L);
            Assert.False(ai.IsValid);
            Assert.Equal(ControlValidationCode.NotHuman, ai.Code);
        }

        [Fact]
        public void PollPendingTakeoversOnlyReturnsDueSeats()
        {
            var registry = new ControlRegistry(new DisconnectPolicy(DisconnectMode.GraceThenAi, 5000L));
            registry.SetController(2, SeatControllerKind.RemoteHuman);
            registry.Disconnect(0, 2000L);
            registry.Disconnect(2, 4000L);

            Assert.Empty(registry.PollPendingTakeovers(6999L));
            var due = registry.PollPendingTakeovers(9000L);

            Assert.Equal(new[] { 0, 2 }, due);
            Assert.True(registry.GetState(0).IsTakenOver);
            Assert.True(registry.GetState(2).IsTakenOver);
        }

        [Fact]
        public void InvalidSeatAndNegativeTimeAreRejected()
        {
            var registry = new ControlRegistry();

            Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetState(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.Disconnect(-1, 0L));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.Disconnect(0, -1L));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DisconnectPolicy(DisconnectMode.GraceThenAi, -1L));
        }
    }
}
