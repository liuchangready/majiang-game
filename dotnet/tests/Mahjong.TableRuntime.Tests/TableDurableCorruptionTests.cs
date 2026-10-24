using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableDurableCorruptionTests
    {
        [Fact]
        public async Task UnknownCheckpointSchemaIsRefused()
        {
            TableCheckpoint bad = DurableHarness.InitialCheckpoint(
                P31Fixtures.Dealt("round-1"),
                DurableHarness.NewLedger(),
                schemaVersion: 99);
            await using DurableHarness harness = await DurableHarness.CreateAsync(initialCheckpoint: bad);
            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CheckpointSchemaUnsupported, result.ReasonCode);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(TableActorLifecycle.Serving, snapshot.Lifecycle);
        }

        [Fact]
        public async Task CorruptCheckpointPayloadIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "c1"))).Status);
            TableCheckpoint good = (await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            byte[] payload = good.StatePayload;
            payload[0] ^= 0xFF;
            harness.Store.ReplaceCheckpointForTest(
                DurableHarness.Room,
                WithStatePayload(good, payload));

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, result.ReasonCode);
        }

        [Fact]
        public async Task EventGapIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(chain[0].Command, 0, "g1"))).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(chain[1].Command, 1, "g2"))).Status);
            harness.Store.RemoveEventForTest(DurableHarness.Room, 1);
            harness.Store.RemoveEventForTest(DurableHarness.Room, 2);

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryEventGap, result.ReasonCode);
        }

        [Fact]
        public async Task DuplicateOrdinalIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(chain[0].Command, 0, "o1"))).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(chain[1].Command, 1, "o2"))).Status);
            harness.Store.SetEventOrdinalForTest(DurableHarness.Room, 2, 0);

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryOrdinalInvalid, result.ReasonCode);
        }

        [Fact]
        public async Task MissingCommandJournalIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "m1"))).Status);
            harness.Store.RemoveCommandForTest(DurableHarness.Room, 1);

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryCommandMissing, result.ReasonCode);
        }

        [Fact]
        public async Task WrongStateHashIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "h1"))).Status);
            harness.Store.SetStateHashForTest(
                DurableHarness.Room,
                TablePayloadSha256.Compute(new byte[] { 1, 2, 3, 4 }));

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryStateHashMismatch, result.ReasonCode);
        }

        [Fact]
        public async Task CorruptCheatLedgerIsRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "l1"))).Status);
            TableCheckpoint good = (await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            byte[] cheat = good.CheatPayload!;
            cheat[4] ^= 0xFF;
            harness.Store.ReplaceCheckpointForTest(
                DurableHarness.Room,
                WithCheatPayload(good, cheat));

            TableRecoveryResult result = await RecoverAsync(harness);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, result.ReasonCode);
        }

        private static async Task<TableRecoveryResult> RecoverAsync(DurableHarness harness)
        {
            harness.Clock.Now += 1000L;
            return await harness.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
        }

        private static TableCheckpoint WithStatePayload(TableCheckpoint good, byte[] payload)
        {
            return new TableCheckpoint(
                good.RoomId,
                good.StateVersion,
                good.LastEventSeq,
                good.StateFingerprint,
                good.RulesetVersion,
                payload,
                TablePayloadSha256.Compute(payload),
                good.ControlVersion,
                good.ControlPayload,
                good.ControlPayloadSha256,
                good.TimingPayload,
                good.TimingPayloadSha256,
                good.CheatPayload,
                good.CheatPayloadSha256,
                good.CommittedFactsPayload,
                good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion);
        }

        private static TableCheckpoint WithCheatPayload(TableCheckpoint good, byte[] payload)
        {
            return new TableCheckpoint(
                good.RoomId,
                good.StateVersion,
                good.LastEventSeq,
                good.StateFingerprint,
                good.RulesetVersion,
                good.StatePayload,
                good.StatePayloadSha256,
                good.ControlVersion,
                good.ControlPayload,
                good.ControlPayloadSha256,
                good.TimingPayload,
                good.TimingPayloadSha256,
                payload,
                TablePayloadSha256.Compute(payload),
                good.CommittedFactsPayload,
                good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion);
        }
    }
}
