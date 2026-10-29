using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class RealSessionIntegrationTests
    {
        [Fact]
        public async Task RealHelloWelcomeBaselineFeedsProjectionAndOwnerQueueSubmission()
        {
            using var channel = new ScriptedTableChannel();
            var outbox = new InMemoryOutboxStore();
            var session = new TableSession(channel, outbox);
            var queue = new TableSessionOperationQueue(session);
            var clock = new ManualTableClock();

            await queue.OpenAsync(new TableSessionOpenRequest("ticket-p43", "build-p43"), CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, queue.State);
            Assert.NotNull(session.PlayerSnapshot);
            Assert.NotNull(queue.Binding);

            var sessionView = new SessionViewState(
                1,
                queue.State,
                queue.Binding,
                session.PlayerSnapshot,
                session.PublicSnapshot,
                session.RoomSnapshot,
                null,
                session.LastError);

            var mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState view = mapper.Project(sessionView);
            Assert.True(view.Identity.IsPrivateAuthorized);
            Assert.Equal("decision-p43", view.Own!.Decision!.DecisionId);

            var factory = new TableCommandIntentFactory(clock);
            var interaction = new TableInteractionState(factory);
            interaction.ApplyConfirmedView(view);
            Assert.True(interaction.SelectAction(view.Own.Decision.Candidates[0]).Accepted);

            var owner = new TableCommandOwnerContext(
                new RequestId("request-live-1"),
                new RequestScopeId(P43TestData.ScopeId));
            TableInteractionOutcome built = interaction.BuildIntent(TableIntentKind.SubmitAction, owner);
            Assert.True(built.Accepted, built.Code);
            TableCommandIntent intent = built.Intent!;

            CommandSubmission submission = await queue.SubmitAsync(intent.Command, CancellationToken.None);
            Assert.True(submission.Sent);
            Assert.False(submission.WasDuplicate);

            Envelope commandEnvelope = Assert.IsType<Envelope>(channel.LastCommand("request-live-1"));
            Assert.Equal(MessageType.Command, commandEnvelope.MessageType);
            Assert.Equal(CommandType.SubmitAction, commandEnvelope.Command.Type);
            Assert.Equal("request-live-1", commandEnvelope.RequestId);
            Assert.Equal(P43TestData.ScopeId, commandEnvelope.Scope.ScopeId);
            Assert.Equal(5UL, commandEnvelope.Command.ExpectedStateVersion);
            Assert.Equal(P43TestData.RoundId, commandEnvelope.Command.ExpectedRoundId);
            Assert.Equal("decision-p43", commandEnvelope.Command.ExpectedDecisionId);
            Assert.Equal(7UL, commandEnvelope.ControlEpoch);
            Assert.Equal(3UL, commandEnvelope.OwnerEpoch);
            Assert.Equal(intent.Command.Payload.CopyBytes(), commandEnvelope.Command.CanonicalPayload.ToByteArray());

            TestPayload payload = new TestPayload(commandEnvelope.Command.CanonicalPayload.ToByteArray());
            Assert.Equal(P43TestData.RoundId, payload.RequireString(CommandPayloadFields.RoundIdField, "roundId"));
            Assert.Equal("decision-p43", payload.RequireString(CommandPayloadFields.DecisionIdField, "decisionId"));
            Assert.Equal(7UL, payload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
            Assert.Equal((uint)ProtocolActionType.DiscardTile, payload.RequireUInt32(CommandPayloadFields.ActionType, "actionType"));
            Assert.Equal("discard-13", payload.RequireString(CommandPayloadFields.OptionId, "optionId"));
            Assert.Equal(new uint[] { 13 }, payload.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds"));

            IReadOnlyList<OutboxEntry> unresolved = await outbox.LoadUnresolvedAsync(CancellationToken.None);
            OutboxEntry entry = Assert.Single(unresolved);
            Assert.Equal(new RequestId("request-live-1"), entry.RequestId);
            Assert.Equal(intent.Command.Payload.Digest, entry.Digest);
            Assert.Equal(intent.Command.Payload.CopyBytes(), entry.CopyCanonicalPayload());

            int commandsBefore = channel.CountSent(MessageType.Command);
            CommandSubmission duplicate = await queue.SubmitAsync(intent.Command, CancellationToken.None);
            Assert.False(duplicate.Sent);
            Assert.True(duplicate.WasDuplicate);
            Assert.Equal(commandsBefore, channel.CountSent(MessageType.Command));
            Assert.Single(await outbox.LoadUnresolvedAsync(CancellationToken.None));
        }

        [Fact]
        public async Task RealSessionBaselineCanBuildTileMarkAndShufflePayloads()
        {
            using var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var queue = new TableSessionOperationQueue(session);
            var clock = new ManualTableClock();

            await queue.OpenAsync(new TableSessionOpenRequest("ticket-p43", "build-p43"), CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);

            var sessionView = new SessionViewState(
                1,
                queue.State,
                queue.Binding,
                session.PlayerSnapshot,
                session.PublicSnapshot,
                session.RoomSnapshot,
                null,
                session.LastError);
            PlayerTableViewState view = new PlayerTableProjectionMapper(clock).Project(sessionView);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent mark = factory.CreateSubmitTileMark(
                view,
                new uint[] { 0, 1, 2, 3 },
                new TableCommandOwnerContext(new RequestId("request-live-mark"), new RequestScopeId(P43TestData.ScopeId)));
            TableCommandIntent plan = factory.CreateSubmitShufflePlan(
                view,
                true,
                new TableCommandOwnerContext(new RequestId("request-live-plan"), new RequestScopeId(P43TestData.ScopeId)));

            Assert.Equal(CommandKind.SubmitTileMark, mark.Kind);
            Assert.Equal(CommandKind.SubmitShufflePlan, plan.Kind);
            CommandSubmission markSubmission = await queue.SubmitAsync(mark.Command, CancellationToken.None);
            CommandSubmission planSubmission = await queue.SubmitAsync(plan.Command, CancellationToken.None);
            Assert.True(markSubmission.Sent);
            Assert.True(planSubmission.Sent);
            Assert.Equal(2, channel.CountSent(MessageType.Command));
        }
    }
}
