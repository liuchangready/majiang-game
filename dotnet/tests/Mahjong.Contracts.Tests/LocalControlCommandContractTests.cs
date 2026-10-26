using System;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Contracts.Tests
{
    public sealed class LocalControlCommandContractTests
    {
        [Fact]
        public void ClaimAndReleaseUseDedicatedTagsAndValidate()
        {
            BusinessPayload claim = CommandPayloadFactory.ClaimControl("r1", 7UL);
            var claimCommand = new ProtocolCommand(
                CommandKind.ClaimControl,
                claim,
                new RequestId("claim-1"),
                new RequestScopeId("scope-1"),
                new StateVersion(0),
                new RoundId("r1"),
                decisionId: null,
                controlEpoch: new ControlEpoch(7));
            Assert.Equal(CommandKind.ClaimControl, claimCommand.Kind);

            BusinessPayload release = CommandPayloadFactory.ReleaseControl("r1", 8UL);
            var releaseCommand = new ProtocolCommand(
                CommandKind.ReleaseControl,
                release,
                new RequestId("release-1"),
                new RequestScopeId("scope-1"),
                new StateVersion(0),
                new RoundId("r1"),
                decisionId: null,
                controlEpoch: new ControlEpoch(8));
            Assert.Equal(CommandKind.ReleaseControl, releaseCommand.Kind);
        }
    }
}
