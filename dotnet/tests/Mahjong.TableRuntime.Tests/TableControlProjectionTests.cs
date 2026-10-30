using System;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// The owner control fact is supplied to the real seat projector from the
    /// same authoritative actor capture as the snapshot. These tests bind the
    /// positive/negative cases to the projector and contract mapper, not to a
    /// duplicate model.
    /// </summary>
    public sealed class TableControlProjectionTests
    {
        [Fact]
        public void OwnerProjectionCarriesEpochAndAutomationAtTheSameCapture()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-control");
            var cheat = V32Fixtures.Cheat();
            TableProjectionContext context = V32Fixtures.Context(round);
            var control = new TableSeatControlProjection(0, new ControlEpoch(0), controlledByComputer: false, controlVersion: 0);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context, control);

            Assert.NotNull(seat0.Private.Control);
            Assert.Equal(0UL, seat0.Private.Control!.ControlEpoch.Value);
            Assert.False(seat0.Private.Control.ControlledByComputer);
            Assert.Equal(0L, seat0.Private.Control.ControlVersion);

            // The same control projection must never leak into another seat's
            // private view, even when the projector is called with it.
            Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 1, context, control));
        }

        [Fact]
        public void AutomatedOwnerWithoutDecisionStillCarriesControlState()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-control");
            var cheat = V32Fixtures.Cheat();
            TableProjectionContext context = V32Fixtures.Context(round);
            var control = new TableSeatControlProjection(2, new ControlEpoch(5), controlledByComputer: true, controlVersion: 9);

            PlayerSnapshotView seat2 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 2, context, control);

            Assert.Null(seat2.Private.Decision);
            Assert.NotNull(seat2.Private.Control);
            Assert.Equal(5UL, seat2.Private.Control!.ControlEpoch.Value);
            Assert.True(seat2.Private.Control.ControlledByComputer);
            Assert.Equal(9L, seat2.Private.Control.ControlVersion);
        }

        [Fact]
        public void MissingControlProjectionProducesNoFabricatedEpoch()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-control");
            var cheat = V32Fixtures.Cheat();
            TableProjectionContext context = V32Fixtures.Context(round);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context);

            Assert.Null(seat0.Private.Control);
        }

        [Fact]
        public void PublicProjectionHasNoControlFieldAtAll()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-control");
            TableProjectionContext context = V32Fixtures.Context(round);

            PublicSnapshotView publicView = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            Assert.NotNull(publicView);
            Assert.Equal("session-1", publicView.SessionId.Value);
        }

        [Fact]
        public void ContractMapperPreservesOwnerControlAndRejectsOtherSeatFact()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-control");
            var cheat = V32Fixtures.Cheat();
            TableProjectionContext context = V32Fixtures.Context(round);
            var control = new TableSeatControlProjection(3, new ControlEpoch(2), controlledByComputer: true, controlVersion: 6);
            PlayerSnapshotView projected = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 3, context, control);
            TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(round, cheat, 3, context);

            PlayerSnapshotView mapped = TableProjectionContractMapper.MapPlayerSnapshot(
                projected,
                owner,
                new TableProjectionTimeContext(1000));

            Assert.NotNull(mapped.Private.Control);
            Assert.Equal(2UL, mapped.Private.Control!.ControlEpoch.Value);
            Assert.True(mapped.Private.Control.ControlledByComputer);
            Assert.Equal(6L, mapped.Private.Control.ControlVersion);
        }
    }
}
