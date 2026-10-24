using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
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

        [Fact]
        public async Task UnmodeledPrivateCheatSubstateIsRefused()
        {
            CheatLedger ledger = DurableHarness.NewLedger();
            byte[] cheat = TableCheatLedgerCheckpointCodec.Encode(
                new TableCheatLedgerCheckpoint(40, ledger, totalVersion: 5L, fingerprint: 123UL));
            TableCheckpoint good = DurableHarness.InitialCheckpoint(
                P31Fixtures.Dealt("round-1"),
                ledger);
            await using DurableHarness harness = await DurableHarness.CreateAsync(
                initialCheckpoint: WithCheatPayload(good, cheat));

            TableRecoveryResult result = await RecoverAsync(harness);

            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, result.ReasonCode);
        }

        [Fact]
        public async Task MissingCommittedFactsIsRefused()
        {
            TableCheckpoint good = DurableHarness.InitialCheckpoint(
                P31Fixtures.Dealt("round-1"),
                DurableHarness.NewLedger());
            await using DurableHarness harness = await DurableHarness.CreateAsync(
                initialCheckpoint: WithFacts(good, null, null));

            TableRecoveryResult result = await RecoverAsync(harness);

            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryStateSinkRequired, result.ReasonCode);
        }

        [Fact]
        public async Task TamperedProjectionFactsAreRefused()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableCheckpoint good = (await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            byte[] facts = good.CommittedFactsPayload!;
            facts[4] ^= 0xFF;
            harness.Store.SetCommittedFactsForTest(
                DurableHarness.Room,
                facts,
                TablePayloadSha256.Compute(facts));
            harness.Store.ReplaceCheckpointForTest(
                DurableHarness.Room,
                WithFacts(good, facts, TablePayloadSha256.Compute(facts)));

            TableRecoveryResult result = await RecoverAsync(harness);

            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, result.ReasonCode);
        }

        [Theory]
        [InlineData("type")]
        [InlineData("seat")]
        [InlineData("detail")]
        [InlineData("version-gap")]
        public async Task CheckpointHistoryPrefixMustBindCapturedDomainEvent(string corruption)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness harness = await DurableHarness.CreateAsync(recoverySink: sink);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2)
                .Select((value, index) => harness.Envelope(value.Command, index, "prefix-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[0])).Status);
            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[1])).Status);

            TableEventRecord[] history = TableCommittedHistoryCodec
                .Decode(checkpoint.CommittedHistoryPayload!).ToArray();
            int index = Array.FindIndex(history, value => value.Type == RoundEventType.TileDiscarded);
            Assert.True(index >= 0);
            TableEventRecord original = history[index];
            history[index] = corruption switch
            {
                "type" => new TableEventRecord(original.Seq, original.CommandVersion, original.Ordinal,
                    RoundEventType.TileDrawn, original.Seat, original.Tile, original.Detail,
                    original.EventSchema, original.Payload),
                "seat" => new TableEventRecord(original.Seq, original.CommandVersion, original.Ordinal,
                    original.Type, (original.Seat + 1) % 4, original.Tile, original.Detail,
                    original.EventSchema, original.Payload),
                "detail" => new TableEventRecord(original.Seq, original.CommandVersion, original.Ordinal,
                    original.Type, original.Seat, original.Tile, "forged-detail",
                    original.EventSchema, original.Payload),
                _ => new TableEventRecord(original.Seq, new StateVersion(2), original.Ordinal,
                    original.Type, original.Seat, original.Tile, original.Detail,
                    original.EventSchema, original.Payload)
            };
            byte[] payload = TableCommittedHistoryCodec.Encode(history);
            harness.Store.ReplaceCheckpointForTest(
                DurableHarness.Room,
                WithHistory(checkpoint, payload));

            TableRecoveryResult result = await RecoverAsync(harness);

            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }

        [Theory]
        [InlineData("first-count")]
        [InlineData("last-count")]
        [InlineData("first-fingerprint")]
        [InlineData("last-fingerprint")]
        public async Task CommandBindingEvidenceMustCoverExactCommandGroups(string corruption)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness harness = await DurableHarness.CreateAsync(recoverySink: sink);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2)
                .Select((value, index) => harness.Envelope(value.Command, index, "group-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[0])).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[1])).Status);
            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            TableCommandBinding[] bindings = TableCommandBindingsCodec
                .Decode(checkpoint.CommittedCommandBindingsPayload!).ToArray();
            Assert.Equal(2, bindings.Length);
            int index = corruption.StartsWith("first", StringComparison.Ordinal) ? 0 : bindings.Length - 1;
            TableCommandBinding original = bindings[index];
            bindings[index] = corruption.EndsWith("count", StringComparison.Ordinal)
                ? new TableCommandBinding(original.CommandVersion, original.EventCount + 1, original.SourceFingerprint)
                : new TableCommandBinding(original.CommandVersion, original.EventCount, original.SourceFingerprint ^ 1UL);
            byte[] payload = TableCommandBindingsCodec.Encode(bindings);
            harness.Store.ReplaceCheckpointForTest(
                DurableHarness.Room,
                WithCommandBindings(checkpoint, payload));

            TableRecoveryResult result = await RecoverAsync(harness);

            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }

        [Theory]
        [InlineData("partial")]
        [InlineData("event-count")]
        [InlineData("group-mismatch")]
        public async Task InvalidSaveBindingsMustNotReplaceCheckpointOrMutateEvidence(string corruption)
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2)
                .Select((value, index) => harness.Envelope(value.Command, index, "save-invalid-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[0])).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(chain[1])).Status);
            TableCheckpoint good = await harness.SaveCheckpointAsync();
            TableCommandBinding[] bindings = TableCommandBindingsCodec
                .Decode(good.CommittedCommandBindingsPayload!).ToArray();
            if (corruption == "partial")
            {
                bindings = new[] { bindings[0] };
            }
            else if (corruption == "event-count")
            {
                TableCommandBinding first = bindings[0];
                bindings[0] = new TableCommandBinding(first.CommandVersion, first.EventCount + 1, first.SourceFingerprint);
            }
            else
            {
                TableCommandBinding first = bindings[0];
                bindings[0] = new TableCommandBinding(2, first.EventCount, first.SourceFingerprint);
            }

            byte[] invalidPayload = TableCommandBindingsCodec.Encode(bindings);
            TableCheckpoint invalid = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq, good.StateFingerprint,
                good.RulesetVersion, good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256, good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                good.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                invalidPayload, TablePayloadSha256.Compute(invalidPayload));
            TableCommittedFactsSnapshot? factsBefore = await harness.Store.LoadCommittedFactsAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            TableTimingJournalRecord? timingBefore = await harness.Store.LoadTimingAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            int commitsBefore = harness.Store.CommitCount;

            TableDurableCommitResult saved = await harness.Store.SaveCheckpointAsync(
                invalid,
                new TableCheckpointFence(
                    DurableHarness.Room,
                    DurableHarness.Owner,
                    new OwnerEpoch(1),
                    good.StateVersion,
                    good.ControlVersion,
                    good.TimingVersion),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableCommitDisposition.Failed, saved.Disposition);
            TableCheckpoint current = (await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(good.CommittedCommandBindingsPayload, current.CommittedCommandBindingsPayload);
            TableCommittedFactsSnapshot? factsAfter = await harness.Store.LoadCommittedFactsAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            Assert.Equal(factsBefore?.Payload, factsAfter?.Payload);
            TableTimingJournalRecord? timingAfter = await harness.Store.LoadTimingAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            Assert.Equal(timingBefore?.TimingVersion, timingAfter?.TimingVersion);
            Assert.Equal(commitsBefore, harness.Store.CommitCount);
            TableRecoveryResult recovery = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
        }

        private static async Task<TableRecoveryResult> RecoverAsync(DurableHarness harness)
        {
            harness.Clock.Now += 1000L;
            return await harness.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
        }

        private static TableCheckpoint WithFacts(
            TableCheckpoint good,
            byte[]? facts,
            TablePayloadSha256? factsSha)
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
                good.CheatPayload,
                good.CheatPayloadSha256,
                facts,
                factsSha,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload,
                good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload,
                good.CommittedCommandBindingsPayloadSha256);
        }

        private static TableCheckpoint WithCommandBindings(TableCheckpoint good, byte[] payload)
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
                good.CheatPayload,
                good.CheatPayloadSha256,
                good.CommittedFactsPayload,
                good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload,
                good.CommittedHistoryPayloadSha256,
                payload,
                TablePayloadSha256.Compute(payload));
        }

        private static TableCheckpoint WithHistory(TableCheckpoint good, byte[] payload)
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
                good.CheatPayload,
                good.CheatPayloadSha256,
                good.CommittedFactsPayload,
                good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs,
                good.BootId,
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                payload,
                TablePayloadSha256.Compute(payload),
                good.CommittedCommandBindingsPayload,
                good.CommittedCommandBindingsPayloadSha256);
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
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload,
                good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload,
                good.CommittedCommandBindingsPayloadSha256);
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
                good.SchemaVersion,
                good.TimingVersion,
                good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload,
                good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload,
                good.CommittedCommandBindingsPayloadSha256);
        }
    }
}
