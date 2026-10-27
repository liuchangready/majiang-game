using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    internal sealed class FaultingFileSystem : ILocalFileSystem
    {
        private readonly SystemLocalFileSystem _inner = new SystemLocalFileSystem();

        public Func<string, Exception?>? BeforeReplace { get; set; }

        public Func<string, Exception?>? AfterReplace { get; set; }

        public Func<string, Exception?>? AfterMove { get; set; }

        public Func<string, Exception?>? BeforeMove { get; set; }

        public Action? DuringReplace { get; set; }

        public Func<string, Exception?>? BeforeCopy { get; set; }

        public Func<string, Exception?>? AfterCopy { get; set; }

        public Func<string, Exception?>? BeforeRead { get; set; }

        public Func<string, Exception?>? BeforeOpenExclusive { get; set; }

        public int MoveAttempts { get; private set; }

        public int CopyAttempts { get; private set; }

        public int ReplaceAttempts { get; private set; }

        public int ReadAttempts { get; private set; }

        public int OpenExclusiveAttempts { get; private set; }

        public int BeforeMoveHits { get; private set; }

        public int AfterMoveHits { get; private set; }

        public int BeforeCopyHits { get; private set; }

        public int AfterCopyHits { get; private set; }

        public int BeforeReplaceHits { get; private set; }

        public int AfterReplaceHits { get; private set; }

        public int DuringReplaceHits { get; private set; }

        public int BeforeReadHits { get; private set; }

        public int BeforeOpenExclusiveHits { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public Stream CreateNew(string path) => _inner.CreateNew(path);

        public Stream OpenRead(string path) => _inner.OpenRead(path);

        public Stream OpenExclusive(string path)
        {
            OpenExclusiveAttempts++;
            Exception? before = BeforeOpenExclusive?.Invoke(path);
            if (before != null)
            {
                BeforeOpenExclusiveHits++;
                throw before;
            }

            return _inner.OpenExclusive(path);
        }

        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);

        public void Move(string source, string destination)
        {
            MoveAttempts++;
            Exception? before = BeforeMove?.Invoke(destination);
            if (before != null)
            {
                BeforeMoveHits++;
                throw before;
            }

            _inner.Move(source, destination);
            Exception? after = AfterMove?.Invoke(destination);
            if (after != null)
            {
                AfterMoveHits++;
                throw after;
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            ReplaceAttempts++;
            Exception? before = BeforeReplace?.Invoke(destination);
            if (before != null)
            {
                BeforeReplaceHits++;
                throw before;
            }

            if (DuringReplace != null)
            {
                DuringReplaceHits++;
                DuringReplace.Invoke();
            }

            _inner.Replace(source, destination, backup);
            Exception? after = AfterReplace?.Invoke(destination);
            if (after != null)
            {
                AfterReplaceHits++;
                throw after;
            }
        }

        public void Copy(string source, string destination, bool overwrite)
        {
            CopyAttempts++;
            Exception? before = BeforeCopy?.Invoke(destination);
            if (before != null)
            {
                BeforeCopyHits++;
                throw before;
            }

            _inner.Copy(source, destination, overwrite);
            Exception? after = AfterCopy?.Invoke(destination);
            if (after != null)
            {
                AfterCopyHits++;
                throw after;
            }
        }

        public void Delete(string path) => _inner.Delete(path);

        public byte[] ReadAllBytes(string path)
        {
            ReadAttempts++;
            Exception? before = BeforeRead?.Invoke(path);
            if (before != null)
            {
                BeforeReadHits++;
                throw before;
            }

            return _inner.ReadAllBytes(path);
        }
    }

    public sealed class LocalTableStoreTests
    {
        [Fact]
        public async Task MoveCommittedButReportsErrorStillLeavesReadablePrimary()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem
            {
                AfterMove = _ => new IOException("injected post-move report error")
            };
            harness.Options.FileSystemForTest = fs;
            LocalTableStore first = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            first.Dispose();
            Assert.True(File.Exists(harness.SavePath));
            Assert.True(fs.MoveAttempts >= 1, "move attempts=" + fs.MoveAttempts);
            Assert.True(fs.AfterMoveHits >= 1, "after move hits=" + fs.AfterMoveHits);

            LocalTableStore reopened = await LocalTableStore.OpenAsync(
                harness.Options,
                harness.Clock,
                null,
                TestContext.Current.CancellationToken);
            try
            {
                Assert.NotNull(await reopened.LoadStateAsync(P41Harness.RoomId, TestContext.Current.CancellationToken));
            }
            finally
            {
                reopened.Dispose();
            }
        }

        [Fact]
        public async Task FirstCreateMoveFailureDoesNotLeaveAnAuthoritativePrimary()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem
            {
                BeforeMove = _ => new IOException("injected move failure")
            };
            harness.Options.FileSystemForTest = fs;
            await Assert.ThrowsAsync<LocalTableException>(async () =>
                await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken));
            Assert.True(fs.MoveAttempts >= 1, "move attempts=" + fs.MoveAttempts);
            Assert.True(fs.BeforeMoveHits >= 1, "before move hits=" + fs.BeforeMoveHits);
            Assert.False(File.Exists(harness.SavePath));
            Assert.False(File.Exists(harness.BackupPath));
            LocalTableStore recovered = await OpenFreshStoreAsync(
                harness,
                new FaultingFileSystem(),
                TestContext.Current.CancellationToken);
            try
            {
                Assert.NotNull(await recovered.LoadStateAsync(P41Harness.RoomId, TestContext.Current.CancellationToken));
            }
            finally
            {
                recovered.Dispose();
            }
        }

        [Fact]
        public async Task FirstCreateCopyFailureKeepsCommittedPrimaryForRepair()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem
            {
                BeforeCopy = _ => new IOException("injected copy failure")
            };
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            TableStateRow original = (await store.LoadStateAsync(P41Harness.RoomId, TestContext.Current.CancellationToken))!;
            store.Dispose();
            Assert.True(fs.CopyAttempts >= 1, "copy attempts=" + fs.CopyAttempts);
            Assert.True(fs.BeforeCopyHits >= 1, "before copy hits=" + fs.BeforeCopyHits);
            Assert.True(File.Exists(harness.SavePath));
            Assert.False(File.Exists(harness.BackupPath));
            LocalTableStore reopened = await LocalTableStore.OpenAsync(
                harness.Options,
                harness.Clock,
                null,
                TestContext.Current.CancellationToken);
            try
            {
                TableStateRow? row = await reopened.LoadStateAsync(
                    P41Harness.RoomId,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(row);
                Assert.Equal(original.StateVersion, row!.StateVersion);
                Assert.Equal(original.StateFingerprint, row.StateFingerprint);
                Assert.Equal(original.StateHash, row.StateHash);
            }
            finally
            {
                reopened.Dispose();
            }
        }

        [Fact]
        public async Task FirstCreateAfterCopyFailureKeepsCommittedPrimaryForRepair()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem
            {
                AfterCopy = _ => new IOException("injected after-copy failure")
            };
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            TableStateRow original = (await store.LoadStateAsync(P41Harness.RoomId, TestContext.Current.CancellationToken))!;
            store.Dispose();
            Assert.True(fs.CopyAttempts >= 1, "copy attempts=" + fs.CopyAttempts);
            Assert.True(fs.AfterCopyHits >= 1, "after copy hits=" + fs.AfterCopyHits);
            Assert.True(File.Exists(harness.SavePath));
            LocalTableStore reopened = await LocalTableStore.OpenAsync(
                harness.Options,
                harness.Clock,
                null,
                TestContext.Current.CancellationToken);
            try
            {
                TableStateRow? row = await reopened.LoadStateAsync(
                    P41Harness.RoomId,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(row);
                Assert.Equal(original.StateVersion, row!.StateVersion);
                Assert.Equal(original.StateFingerprint, row.StateFingerprint);
                Assert.Equal(original.StateHash, row.StateHash);
            }
            finally
            {
                reopened.Dispose();
            }
        }

        [Fact]
        public async Task FirstCreateWritesPrimaryAndPreviousCopy()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            try
            {
                Assert.True(File.Exists(harness.SavePath));
                Assert.True(File.Exists(harness.BackupPath));
                Assert.True(new FileInfo(harness.SavePath).Length > 32);
                Assert.True(fs.MoveAttempts >= 1, "move attempts=" + fs.MoveAttempts);
                Assert.True(fs.CopyAttempts >= 1, "copy attempts=" + fs.CopyAttempts);
            }
            finally
            {
                store.Dispose();
            }
        }

        [Fact]
        public async Task ReplaceFailureBeforeCommitFencesTheStore()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            try
            {
                byte[] before = File.ReadAllBytes(harness.SavePath);
                fs.BeforeReplace = _ => new IOException("injected replace failure");
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);
                TableDurableCommitResult result = await SaveCheckpointAsync(store, checkpoint, expectedTiming: 1L, TestContext.Current.CancellationToken);

                Assert.True(result.Disposition == TableCommitDisposition.OutcomeUnknown, "disposition=" + result.Disposition + " reason=" + result.ReasonCode + " detail=" + result.Detail);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);
                Assert.True(fs.BeforeReplaceHits >= 1, "before replace hits=" + fs.BeforeReplaceHits);
                Assert.Equal(before.Length, new FileInfo(harness.SavePath).Length);

                await Assert.ThrowsAsync<LocalTableException>(
                    async () => await SaveCheckpointAsync(store, checkpoint, expectedTiming: 1L, TestContext.Current.CancellationToken));
            }
            finally
            {
                store.Dispose();
            }
        }

        [Fact]
        public async Task CorruptPrimaryWithGoodPreviousCopyIsUnknownOnReplaceFailure()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            try
            {
                byte[] corrupt = File.ReadAllBytes(harness.SavePath);
                corrupt[corrupt.Length - 1] ^= 0x01;
                File.WriteAllBytes(harness.SavePath, corrupt);
                fs.BeforeReplace = _ => new IOException("injected after primary corruption");
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);
                TableDurableCommitResult result = await SaveCheckpointAsync(
                    store,
                    checkpoint,
                    expectedTiming: 1L,
                    TestContext.Current.CancellationToken);
                Assert.True(
                    result.Disposition == TableCommitDisposition.OutcomeUnknown,
                    "corrupt primary + readable prev must be Unknown, actual=" + result.Disposition);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);
                Assert.True(fs.BeforeReplaceHits >= 1, "before replace hits=" + fs.BeforeReplaceHits);
            }
            finally
            {
                store.Dispose();
            }
        }

        [Fact]
        public async Task ReplaceFailureAfterAtomicReplaceIsRecognizedAsCommitted()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            try
            {
                fs.AfterReplace = _ => new IOException("injected after replace");
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);
                TableDurableCommitResult result = await SaveCheckpointAsync(store, checkpoint, expectedTiming: 1L, TestContext.Current.CancellationToken);

                Assert.True(result.Disposition == TableCommitDisposition.Committed, "disposition=" + result.Disposition + " reason=" + result.ReasonCode + " detail=" + result.Detail);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);
                Assert.True(fs.AfterReplaceHits >= 1, "after replace hits=" + fs.AfterReplaceHits);
                TableStateRow? row = await store.LoadStateAsync(
                    P41Harness.RoomId,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(row);
                Assert.Equal(2L, row!.TimingVersion);
            }
            finally
            {
                store.Dispose();
            }
        }

        [Fact]
        public async Task CancellationInsideTheAtomicReplaceStillInstallsCommittedFacts()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            try
            {
                using var cts = new CancellationTokenSource();
                fs.DuringReplace = cts.Cancel;
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);
                TableDurableCommitResult result = await SaveCheckpointAsync(
                    store,
                    checkpoint,
                    expectedTiming: 1L,
                    cts.Token);

                Assert.True(result.Disposition == TableCommitDisposition.Committed, "disposition=" + result.Disposition + " reason=" + result.ReasonCode + " detail=" + result.Detail);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);
                Assert.True(fs.DuringReplaceHits >= 1, "during replace hits=" + fs.DuringReplaceHits);
                TableStateRow? row = await store.LoadStateAsync(
                    P41Harness.RoomId,
                    CancellationToken.None);
                Assert.Equal(2L, row!.TimingVersion);
            }
            finally
            {
                store.Dispose();
            }
        }

        [Fact]
        public async Task NewerFileFormatIsRejectedInsteadOfFallingBackToPrev()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            store.Dispose();

            byte[] primary = File.ReadAllBytes(harness.SavePath);
            primary[4] = 2;
            File.WriteAllBytes(harness.SavePath, primary);

            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(
                    harness.Options,
                    harness.Clock,
                    fs,
                    TestContext.Current.CancellationToken));
            Assert.Equal(LocalTableErrorKind.CorruptState, error.ErrorKind);
        }

        [Fact]
        public async Task CreateRoom_FencesAndPayloadValidation()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                await Assert.ThrowsAsync<ArgumentNullException>(
                    async () => await store.CreateRoomAsync(null!, ct));

                var facts = new LocalSnapshotFacts(harness.Options.Environment!, harness.Clock);
                var round = facts.CreateInitialRound(harness.Options);
                TableCheckpoint checkpoint = facts.CreateInitialCheckpoint(harness.Options, round);

                Assert.Equal(checkpoint.TimingPayloadSha256, TablePayloadSha256.Compute(checkpoint.TimingPayload));
                Assert.Equal(checkpoint.StatePayloadSha256, TablePayloadSha256.Compute(checkpoint.StatePayload));
                if (checkpoint.CommittedFactsPayload != null)
                {
                    Assert.Equal(checkpoint.CommittedFactsPayloadSha256, TablePayloadSha256.Compute(checkpoint.CommittedFactsPayload));
                }

                await store.CreateRoomAsync(checkpoint, ct);

                LocalTableException duplicate = await Assert.ThrowsAsync<LocalTableException>(
                    async () => await store.CreateRoomAsync(checkpoint, ct));
                Assert.Equal(LocalTableErrorKind.CorruptState, duplicate.ErrorKind);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(checkpoint.StateVersion, row.StateVersion);
                Assert.Equal(checkpoint.TimingVersion, row.TimingVersion);
                Assert.Equal(checkpoint.ControlVersion, row.ControlVersion);
                Assert.Equal(checkpoint.StatePayloadSha256, row.StateHash);
                Assert.Equal(checkpoint.StateFingerprint, row.StateFingerprint);

                TableTimingJournalRecord timingRecord = (await store.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(checkpoint.BootId, timingRecord.BootId);
                Assert.Equal(checkpoint.TimingVersion, timingRecord.TimingVersion);
                Assert.Equal(checkpoint.CapturedMonotonicMillis, timingRecord.CapturedMonotonicMillis);

                TableCommittedFactsSnapshot factsSnapshot = (await store.LoadCommittedFactsAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(checkpoint.CommittedFactsPayloadSha256, factsSnapshot.Sha256);
            }

            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow row = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(row);
                TableCheckpoint loadedCp = (await reopened.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(row.StateVersion, loadedCp.StateVersion);
                Assert.Equal(row.StateHash, loadedCp.StatePayloadSha256);
                Assert.Equal(row.StateFingerprint, loadedCp.StateFingerprint);
                Assert.Equal(row.ControlVersion, loadedCp.ControlVersion);
            }
        }

        [Fact]
        public async Task Commit_InternalCommandWithoutReceipt_ValidatesFencesAndReplaysCleanly()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                TableLeaseAcquireResult leaseResult = await store.AcquireAsync(
                    P41Harness.RoomId,
                    "owner-local",
                    TimeSpan.FromMinutes(5),
                    ct);
                Assert.True(leaseResult.Acquired);

                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                TableTimingCheckpoint nextTiming = AdvanceTiming(timing);

                // 1) Owner fence mismatch
                var wrongOwnerReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "wrong-owner",
                    store.LeaseEpoch,
                    checkpoint.StateVersion.Value,
                    2L,
                    nextTiming,
                    checkpoint);
                TableDurableCommitResult wrongOwnerRes = await store.CommitAsync(wrongOwnerReq, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongOwnerRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongOwnerRes.ReasonCode);

                // 2) Owner epoch mismatch
                var wrongEpochReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    new OwnerEpoch(999),
                    checkpoint.StateVersion.Value,
                    2L,
                    nextTiming,
                    checkpoint);
                TableDurableCommitResult wrongEpochRes = await store.CommitAsync(wrongEpochReq, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongEpochRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongEpochRes.ReasonCode);

                // 3) State version mismatch
                var staleStateReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    999UL,
                    2L,
                    nextTiming,
                    checkpoint);
                TableDurableCommitResult staleStateRes = await store.CommitAsync(staleStateReq, ct);
                Assert.Equal(TableCommitDisposition.StaleState, staleStateRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleStateRes.ReasonCode);

                // 4) Timing version mismatch
                var staleTimingReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion.Value,
                    999L,
                    nextTiming,
                    checkpoint);
                TableDurableCommitResult staleTimingRes = await store.CommitAsync(staleTimingReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleTimingRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleTimingRes.ReasonCode);

                // 5) Facts payload vs hash domain mismatch
                var tamperedFactsReq = new TableDurableCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    new StateVersion(checkpoint.StateVersion.Value),
                    receipt: null,
                    internalCommand: true,
                    events: BuildInternalCommitRequest(P41Harness.RoomId, "owner-local", store.LeaseEpoch, checkpoint.StateVersion.Value, 2L, nextTiming, checkpoint).Events,
                    commandJournal: BuildInternalCommitRequest(P41Harness.RoomId, "owner-local", store.LeaseEpoch, checkpoint.StateVersion.Value, 2L, nextTiming, checkpoint).CommandJournal,
                    outbox: null,
                    checkpoint: null,
                    nextStatePayload: checkpoint.StatePayload,
                    nextStateFingerprint: checkpoint.StateFingerprint + 1UL,
                    terminal: false,
                    timing: nextTiming,
                    timingVersion: 2L,
                    committedFactsPayload: checkpoint.CommittedFactsPayload,
                    committedFactsPayloadSha256: new TablePayloadSha256(new byte[32]),
                    nextCheatVersion: 0L,
                    nextCheatFingerprint: 0UL);
                TableDurableCommitResult tamperedFactsRes = await store.CommitAsync(tamperedFactsReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, tamperedFactsRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, tamperedFactsRes.ReasonCode);

                // 6) Timing round mismatch vs next state payload
                var wrongRoundTiming = new TableTimingCheckpoint(
                    "mismatched-round-id",
                    nextTiming.SeatReservesMillis,
                    nextTiming.Decisions,
                    nextTiming.CapturedAtUtcMs,
                    nextTiming.CapturedMonotonicMillis,
                    nextTiming.BootId);
                var wrongRoundReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion.Value,
                    2L,
                    wrongRoundTiming,
                    checkpoint);
                TableDurableCommitResult wrongRoundRes = await store.CommitAsync(wrongRoundReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, wrongRoundRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, wrongRoundRes.ReasonCode);

                // Verify negative fences caused no side effects on store state
                TableStateRow unmutatedRow = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(0UL, unmutatedRow.StateVersion.Value);
                Assert.Equal(1L, unmutatedRow.TimingVersion);

                // 7) Positive internal command commit (InternalCommand = true, Receipt = null)
                var validInternalReq = BuildInternalCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion.Value,
                    2L,
                    nextTiming,
                    checkpoint);
                TableDurableCommitResult commitRes = await store.CommitAsync(validInternalReq, ct);
                Assert.True(commitRes.IsCommitted, "reason=" + commitRes.ReasonCode + " detail=" + commitRes.Detail);
                Assert.Null(commitRes.Receipt);

                TableDurableReceipt? noReceipt = await store.TryGetAsync(
                    "local-seat0",
                    new RequestScopeId("scope-internal"),
                    new RequestId("internal-req-1"),
                    ct);
                Assert.Null(noReceipt);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, row.StateVersion.Value);
                Assert.Equal(2L, row.TimingVersion);
                Assert.True(row.StateFingerprint != checkpoint.StateFingerprint);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);

                // Facts payload same-domain equivalence verification
                TableCommittedFactsSnapshot factsSnapshot = (await store.LoadCommittedFactsAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(factsSnapshot);
                Assert.Equal(checkpoint.CommittedFactsPayloadSha256, factsSnapshot.Sha256);
                Assert.True(checkpoint.CommittedFactsPayload.AsSpan().SequenceEqual(factsSnapshot.Payload));
            }

            // 6) Replay from disk: cold restart hydrates operation with InternalCommand = true
            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow replayedRow = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, replayedRow.StateVersion.Value);
                Assert.Equal(2L, replayedRow.TimingVersion);
                Assert.True(replayedRow.StateFingerprint != 0UL);
            }
        }

        [Fact]
        public async Task SaveCheckpoint_FencesPayloadsAndTailCompaction()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);

                // 1) Owner fence negative
                var wrongOwnerFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "wrong-owner",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L);
                TableDurableCommitResult wrongOwnerRes = await store.SaveCheckpointAsync(checkpoint, wrongOwnerFence, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongOwnerRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongOwnerRes.ReasonCode);

                // 2) State fence negative
                var staleStateFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    new StateVersion(999),
                    checkpoint.ControlVersion,
                    1L);
                TableDurableCommitResult staleStateRes = await store.SaveCheckpointAsync(checkpoint, staleStateFence, ct);
                Assert.Equal(TableCommitDisposition.StaleState, staleStateRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleStateRes.ReasonCode);

                // 3) Control fence negative
                var staleControlFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    999L,
                    1L);
                TableDurableCommitResult staleControlRes = await store.SaveCheckpointAsync(checkpoint, staleControlFence, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleControlRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleControlVersion, staleControlRes.ReasonCode);

                // 4) Timing fence negative
                var staleTimingFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    999L);
                TableDurableCommitResult staleTimingRes = await store.SaveCheckpointAsync(checkpoint, staleTimingFence, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleTimingRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleTimingRes.ReasonCode);

                // 5) Timing payload hash mismatch rejected at checkpoint construction boundary
                byte[] tamperedTiming = (byte[])checkpoint.TimingPayload.Clone();
                tamperedTiming[tamperedTiming.Length - 1] ^= 0x01;
                Assert.Throws<ArgumentException>(() => new TableCheckpoint(
                    checkpoint.RoomId,
                    checkpoint.StateVersion,
                    checkpoint.LastEventSeq,
                    checkpoint.StateFingerprint,
                    checkpoint.RulesetVersion,
                    checkpoint.StatePayload,
                    checkpoint.StatePayloadSha256,
                    checkpoint.ControlVersion,
                    checkpoint.ControlPayload,
                    checkpoint.ControlPayloadSha256,
                    tamperedTiming,
                    checkpoint.TimingPayloadSha256,
                    checkpoint.CheatPayload,
                    checkpoint.CheatPayloadSha256,
                    checkpoint.CommittedFactsPayload,
                    checkpoint.CommittedFactsPayloadSha256,
                    checkpoint.CreatedAtUtcMs,
                    checkpoint.BootId,
                    checkpoint.SchemaVersion,
                    checkpoint.TimingVersion,
                    checkpoint.CapturedMonotonicMillis,
                    checkpoint.CommittedHistoryPayload,
                    checkpoint.CommittedHistoryPayloadSha256,
                    checkpoint.CommittedCommandBindingsPayload,
                    checkpoint.CommittedCommandBindingsPayloadSha256));

                var validFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L);

                // 6) Positive SaveCheckpoint: compacts tail
                TableDurableCommitResult saveRes = await store.SaveCheckpointAsync(checkpoint, validFence, ct);
                Assert.True(saveRes.IsCommitted, "reason=" + saveRes.ReasonCode + " detail=" + saveRes.Detail);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, row.TimingVersion);

                TableCheckpoint loadedCp = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, loadedCp.TimingVersion);
            }

            // 7) Cold reload matches new checkpoint baseline
            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow row = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, row.TimingVersion);
                TableCheckpoint loadedCp = (await reopened.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, loadedCp.TimingVersion);
            }
        }

        [Fact]
        public async Task CommitControl_FencesPayloadsAndAdvancesControlVersion()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                (TableControlCommitRequest request, TableCheckpoint candidate) = BuildControlRequest(
                    store,
                    checkpoint,
                    currentControlVersion: 0L,
                    expectedTimingVersion: 1L);

                // 1) Owner mismatch
                var wrongOwnerReq = new TableControlCommitRequest(
                    request.RoomId,
                    request.OwnerEpoch,
                    request.ExpectedControlVersion,
                    request.ControlVersion,
                    request.StateVersion,
                    request.RoundId,
                    request.Change,
                    "wrong-owner");
                TableDurableCommitResult wrongOwnerRes = await store.CommitControlAsync(wrongOwnerReq, candidate, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongOwnerRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongOwnerRes.ReasonCode);

                // 2) Owner epoch mismatch
                var wrongEpochReq = new TableControlCommitRequest(
                    request.RoomId,
                    new OwnerEpoch(999),
                    request.ExpectedControlVersion,
                    request.ControlVersion,
                    request.StateVersion,
                    request.RoundId,
                    request.Change,
                    request.OwnerInstance);
                TableDurableCommitResult wrongEpochRes = await store.CommitControlAsync(wrongEpochReq, candidate, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongEpochRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongEpochRes.ReasonCode);

                // 3) State version mismatch
                var staleStateReq = new TableControlCommitRequest(
                    request.RoomId,
                    request.OwnerEpoch,
                    request.ExpectedControlVersion,
                    request.ControlVersion,
                    new StateVersion(999),
                    request.RoundId,
                    request.Change,
                    request.OwnerInstance);
                TableDurableCommitResult staleStateRes = await store.CommitControlAsync(staleStateReq, candidate, ct);
                Assert.Equal(TableCommitDisposition.StaleState, staleStateRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleStateRes.ReasonCode);

                // 4) Control version mismatch
                var staleChange = new TableControlEvent(
                    1000L,
                    request.RoundId,
                    0,
                    TableControlChangeKind.ExplicitTakeover,
                    null, null, null, null, null, null, null,
                    1000L, 1000L, "control-policy");
                var staleControlReq = new TableControlCommitRequest(
                    request.RoomId,
                    request.OwnerEpoch,
                    999L,
                    1000L,
                    request.StateVersion,
                    request.RoundId,
                    staleChange,
                    request.OwnerInstance);
                TableDurableCommitResult staleControlRes = await store.CommitControlAsync(staleControlReq, candidate, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleControlRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleControlVersion, staleControlRes.ReasonCode);

                // 5) Candidate timing version mismatch
                var staleTimingCandidate = new TableCheckpoint(
                    candidate.RoomId,
                    candidate.StateVersion,
                    candidate.LastEventSeq,
                    candidate.StateFingerprint,
                    candidate.RulesetVersion,
                    candidate.StatePayload,
                    candidate.StatePayloadSha256,
                    candidate.ControlVersion,
                    candidate.ControlPayload,
                    candidate.ControlPayloadSha256,
                    candidate.TimingPayload,
                    candidate.TimingPayloadSha256,
                    candidate.CheatPayload,
                    candidate.CheatPayloadSha256,
                    candidate.CommittedFactsPayload,
                    candidate.CommittedFactsPayloadSha256,
                    candidate.CreatedAtUtcMs,
                    candidate.BootId,
                    candidate.SchemaVersion,
                    999L,
                    candidate.CapturedMonotonicMillis,
                    candidate.CommittedHistoryPayload,
                    candidate.CommittedHistoryPayloadSha256,
                    candidate.CommittedCommandBindingsPayload,
                    candidate.CommittedCommandBindingsPayloadSha256);
                TableDurableCommitResult staleTimingRes = await store.CommitControlAsync(request, staleTimingCandidate, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleTimingRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, staleTimingRes.ReasonCode);

                // 6) Tampered candidate timing payload hash
                byte[] tamperedCandidateTiming = (byte[])candidate.TimingPayload.Clone();
                tamperedCandidateTiming[tamperedCandidateTiming.Length - 1] ^= 0x01;
                Assert.Throws<ArgumentException>(() => new TableCheckpoint(
                    candidate.RoomId,
                    candidate.StateVersion,
                    candidate.LastEventSeq,
                    candidate.StateFingerprint,
                    candidate.RulesetVersion,
                    candidate.StatePayload,
                    candidate.StatePayloadSha256,
                    candidate.ControlVersion,
                    candidate.ControlPayload,
                    candidate.ControlPayloadSha256,
                    tamperedCandidateTiming,
                    candidate.TimingPayloadSha256,
                    candidate.CheatPayload,
                    candidate.CheatPayloadSha256,
                    candidate.CommittedFactsPayload,
                    candidate.CommittedFactsPayloadSha256,
                    candidate.CreatedAtUtcMs,
                    candidate.BootId,
                    candidate.SchemaVersion,
                    candidate.TimingVersion,
                    candidate.CapturedMonotonicMillis,
                    candidate.CommittedHistoryPayload,
                    candidate.CommittedHistoryPayloadSha256,
                    candidate.CommittedCommandBindingsPayload,
                    candidate.CommittedCommandBindingsPayloadSha256));

                // 7) Tampered candidate facts payload hash
                byte[] tamperedCandidateFacts = (byte[])candidate.CommittedFactsPayload!.Clone();
                tamperedCandidateFacts[tamperedCandidateFacts.Length - 1] ^= 0x01;
                Assert.Throws<ArgumentException>(() => new TableCheckpoint(
                    candidate.RoomId,
                    candidate.StateVersion,
                    candidate.LastEventSeq,
                    candidate.StateFingerprint,
                    candidate.RulesetVersion,
                    candidate.StatePayload,
                    candidate.StatePayloadSha256,
                    candidate.ControlVersion,
                    candidate.ControlPayload,
                    candidate.ControlPayloadSha256,
                    candidate.TimingPayload,
                    candidate.TimingPayloadSha256,
                    candidate.CheatPayload,
                    candidate.CheatPayloadSha256,
                    tamperedCandidateFacts,
                    candidate.CommittedFactsPayloadSha256,
                    candidate.CreatedAtUtcMs,
                    candidate.BootId,
                    candidate.SchemaVersion,
                    candidate.TimingVersion,
                    candidate.CapturedMonotonicMillis,
                    candidate.CommittedHistoryPayload,
                    candidate.CommittedHistoryPayloadSha256,
                    candidate.CommittedCommandBindingsPayload,
                    candidate.CommittedCommandBindingsPayloadSha256));

                // Verify negative fences caused no side effects on store state
                TableStateRow stateBefore = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(0L, stateBefore.ControlVersion);
                Assert.Equal(1L, stateBefore.TimingVersion);

                // 8) Positive commit
                TableDurableCommitResult controlRes = await store.CommitControlAsync(request, candidate, ct);
                Assert.True(controlRes.IsCommitted, "reason=" + controlRes.ReasonCode + " detail=" + controlRes.Detail);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1L, row.ControlVersion);
                Assert.Equal(2L, row.TimingVersion);

                TableCommittedFactsSnapshot factsSnapshot = (await store.LoadCommittedFactsAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(factsSnapshot);
                Assert.Equal(candidate.CommittedFactsPayloadSha256, factsSnapshot.Sha256);
            }

            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow row = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1L, row.ControlVersion);
                Assert.Equal(2L, row.TimingVersion);
            }
        }

        [Fact]
        public async Task AppendTiming_FencesClockPayloadAndHash()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                TableTimingCheckpoint nextTiming = AdvanceTiming(timing);

                // 1) Owner mismatch
                var wrongOwnerReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "wrong-owner",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L,
                    nextTiming);
                TableDurableCommitResult wrongOwnerRes = await store.AppendTimingAsync(wrongOwnerReq, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongOwnerRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongOwnerRes.ReasonCode);

                // 2) Owner epoch mismatch
                var wrongEpochReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    new OwnerEpoch(999),
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L,
                    nextTiming);
                TableDurableCommitResult wrongEpochRes = await store.AppendTimingAsync(wrongEpochReq, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongEpochRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongEpochRes.ReasonCode);

                // 3) State version mismatch
                var staleStateReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    new StateVersion(999),
                    checkpoint.ControlVersion,
                    1L,
                    nextTiming);
                TableDurableCommitResult staleStateRes = await store.AppendTimingAsync(staleStateReq, ct);
                Assert.Equal(TableCommitDisposition.StaleState, staleStateRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleStateRes.ReasonCode);

                // 4) Control version mismatch
                var staleControlReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    999L,
                    1L,
                    nextTiming);
                TableDurableCommitResult staleControlRes = await store.AppendTimingAsync(staleControlReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleControlRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleControlVersion, staleControlRes.ReasonCode);

                // 5) Timing version mismatch
                var staleTimingReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    999L,
                    nextTiming);
                TableDurableCommitResult staleTimingRes = await store.AppendTimingAsync(staleTimingReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, staleTimingRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleTimingRes.ReasonCode);

                // 6) Empty boot id negative
                var emptyBootTiming = new TableTimingCheckpoint(
                    timing.RoundId,
                    timing.SeatReservesMillis,
                    timing.Decisions,
                    timing.CapturedAtUtcMs,
                    timing.CapturedMonotonicMillis,
                    string.Empty);
                var emptyBootReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L,
                    emptyBootTiming);
                TableDurableCommitResult emptyBootRes = await store.AppendTimingAsync(emptyBootReq, ct);
                Assert.Equal(TableCommitDisposition.Failed, emptyBootRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.RecoveryTimingInvalid, emptyBootRes.ReasonCode);

                // Verify negative fences caused no side effects on store timing or state
                TableTimingJournalRecord timingBefore = (await store.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1L, timingBefore.TimingVersion);
                TableStateRow stateBefore = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1L, stateBefore.TimingVersion);

                // 7) Positive AppendTiming
                var validTimingReq = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L,
                    nextTiming);
                TableDurableCommitResult timingRes = await store.AppendTimingAsync(validTimingReq, ct);
                Assert.True(timingRes.IsCommitted, "reason=" + timingRes.ReasonCode + " detail=" + timingRes.Detail);

                TableTimingJournalRecord loadedTiming = (await store.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, loadedTiming.TimingVersion);
                Assert.Equal(nextTiming.BootId, loadedTiming.BootId);
                Assert.Equal(nextTiming.CapturedMonotonicMillis, loadedTiming.CapturedMonotonicMillis);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, row.TimingVersion);
            }

            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow row = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, row.TimingVersion);
                TableTimingJournalRecord loadedTiming = (await reopened.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, loadedTiming.TimingVersion);
            }
        }

        [Fact]
        public async Task SaveCheckpoint_TimingProgression_ColdRecoveryContinuous()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Clock starts at known baseline
            harness.Clock.UtcNowMs = 1_700_000_000_000L;
            harness.Clock.MonotonicMillis = 1000L;

            // t = 1: Room created
            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct);
            try
            {
                TableTimingJournalRecord t1 = (await store.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1L, t1.TimingVersion);
                Assert.Equal(1_700_000_000_000UL, t1.CapturedAtUtcMs);
                Assert.Equal(0L, t1.CapturedMonotonicMillis);

                // t -> t+1: Real clock advances 500ms
                harness.Clock.UtcNowMs += 500L;
                harness.Clock.MonotonicMillis = 500L;

                // SaveCheckpoint advances timing to 2 with real clock
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store, harness.Clock);
                TableDurableCommitResult saveResult = await SaveCheckpointAsync(
                    store,
                    checkpoint,
                    expectedTiming: 1L,
                    ct);
                Assert.True(saveResult.IsCommitted);

                TableTimingJournalRecord t2 = (await store.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, t2.TimingVersion);
                Assert.Equal(t1.BootId, t2.BootId);
                Assert.Equal(1_700_000_000_500UL, t2.CapturedAtUtcMs);
                Assert.Equal(500L, t2.CapturedMonotonicMillis);
            }
            finally
            {
                store.Dispose();
            }

            // Cold recovery: reopened store must load timing version 2 and exact clock values
            LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct);
            TableTimingCheckpoint nextTiming;
            try
            {
                TableTimingJournalRecord coldT2 = (await reopened.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, coldT2.TimingVersion);
                Assert.Equal(1_700_000_000_500UL, coldT2.CapturedAtUtcMs);
                Assert.Equal(500L, coldT2.CapturedMonotonicMillis);
                TableStateRow coldState = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2L, coldState.TimingVersion);

                // Advance real clock another 1000ms: cold recovery continuous to t+2 (timing version 3)
                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis = 1500L;

                nextTiming = AdvanceTiming(coldT2.Timing, harness.Clock);
                Assert.Equal(1_700_000_001_500UL, nextTiming.CapturedAtUtcMs);
                Assert.Equal(1500L, nextTiming.CapturedMonotonicMillis);

                var appendReq = new TableTimingAppendRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    reopened.LeaseEpoch,
                    coldState.StateVersion,
                    coldState.ControlVersion,
                    2L,
                    nextTiming);
                TableDurableCommitResult appendRes = await reopened.AppendTimingAsync(appendReq, ct);
                Assert.True(appendRes.IsCommitted);

                TableTimingJournalRecord t3 = (await reopened.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3L, t3.TimingVersion);
                Assert.Equal(coldT2.BootId, t3.BootId);
                Assert.Equal(1_700_000_001_500UL, t3.CapturedAtUtcMs);
                Assert.Equal(1500L, t3.CapturedMonotonicMillis);
            }
            finally
            {
                reopened.Dispose();
            }

            // Second cold recovery: asserts exact persistence of t+2 = 3 with exact real clock values
            using (LocalTableStore finalStore = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableTimingJournalRecord finalTiming = (await finalStore.LoadTimingAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3L, finalTiming.TimingVersion);
                Assert.Equal(1_700_000_001_500UL, finalTiming.CapturedAtUtcMs);
                Assert.Equal(1500L, finalTiming.CapturedMonotonicMillis);
                TableStateRow finalState = (await finalStore.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3L, finalState.TimingVersion);
            }
        }

        [Fact]
        public async Task TransientIoExceptionAndPermissionFailure_AreUnavailableNeverTreatedAsCorruption()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct);
            store.Dispose();

            // 1) Transient IOException on read
            fs.BeforeRead = _ => new IOException("transient device read error");
            LocalTableException ioEx = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct));
            Assert.Equal(LocalTableErrorKind.Unavailable, ioEx.ErrorKind);
            Assert.True(fs.ReadAttempts >= 1, "read attempts=" + fs.ReadAttempts);
            Assert.True(fs.BeforeReadHits >= 1, "before read hits=" + fs.BeforeReadHits);
            Assert.True(File.Exists(harness.SavePath));

            // 2) Transient UnauthorizedAccessException on read
            fs.BeforeRead = _ => new UnauthorizedAccessException("transient acl error");
            LocalTableException aclEx = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct));
            Assert.Equal(LocalTableErrorKind.Unavailable, aclEx.ErrorKind);
            Assert.True(fs.ReadAttempts >= 2, "read attempts=" + fs.ReadAttempts);
            Assert.True(fs.BeforeReadHits >= 2, "before read hits=" + fs.BeforeReadHits);

            // 3) Permission failure on OpenExclusive
            fs.BeforeRead = null;
            fs.BeforeOpenExclusive = _ => new UnauthorizedAccessException("exclusive lock acl denied");
            LocalTableException lockEx = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct));
            Assert.Equal(LocalTableErrorKind.LeaseConflict, lockEx.ErrorKind);
            Assert.True(fs.OpenExclusiveAttempts >= 1, "open exclusive attempts=" + fs.OpenExclusiveAttempts);
            Assert.True(fs.BeforeOpenExclusiveHits >= 1, "before open exclusive hits=" + fs.BeforeOpenExclusiveHits);

            // 4) Clearing faults restores healthy access
            fs.BeforeOpenExclusive = null;
            using (LocalTableStore recovered = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow? row = await recovered.LoadStateAsync(P41Harness.RoomId, ct);
                Assert.NotNull(row);
            }
        }

        [Fact]
        public async Task OutcomeUnknown_FencesStore_ThenReconcilesAgainstDiskEvidence()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = await BuildNextCheckpointAsync(store);

                // Corrupt primary so that replace failure leaves primary unreadable
                byte[] corrupt = File.ReadAllBytes(harness.SavePath);
                corrupt[corrupt.Length - 1] ^= 0x01;
                File.WriteAllBytes(harness.SavePath, corrupt);

                // Replace fails before commit
                fs.BeforeReplace = _ => new IOException("injected catastrophic replace failure");
                TableDurableCommitResult unknownResult = await SaveCheckpointAsync(
                    store,
                    checkpoint,
                    expectedTiming: 1L,
                    ct);

                Assert.Equal(TableCommitDisposition.OutcomeUnknown, unknownResult.Disposition);
                Assert.True(fs.ReplaceAttempts >= 1, "replace attempts=" + fs.ReplaceAttempts);
                Assert.True(fs.BeforeReplaceHits >= 1, "before replace hits=" + fs.BeforeReplaceHits);

                // Store is now fenced: next mutation throws Unavailable
                LocalTableException fencedError = await Assert.ThrowsAsync<LocalTableException>(
                    async () => await SaveCheckpointAsync(store, checkpoint, expectedTiming: 1L, ct));
                Assert.Equal(LocalTableErrorKind.Unavailable, fencedError.ErrorKind);
                Assert.Contains("fenced", fencedError.Message, StringComparison.OrdinalIgnoreCase);

                // TryGetAsync reconciles against physical disk evidence
                // Since primary is corrupt and prev cannot prove receipt, TryGetAsync throws Unavailable
                await Assert.ThrowsAsync<LocalTableException>(
                    async () => await store.TryGetAsync(
                        "local-seat0",
                        new RequestScopeId("scope-1"),
                        new RequestId("req-1"),
                        ct));
            }
        }

        [Fact]
        public async Task Idempotency_ActionsCheatsSettlements_ExecuteOnce_RejectConflictingBytes_RejectExpiredScope()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;
            ulong nowUtc = (ulong)harness.Clock.UtcNowMs;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);

                string accountId = "player-account-0";
                var validScope = store.IssueScope(
                    accountId,
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    issuedAtUtcMs: nowUtc - 10_000UL,
                    expiresAtUtcMs: nowUtc + 600_000UL,
                    scopeId: "scope-valid-1");

                // --- 1. Normal Action: DiscardTile with real domain state change ---
                var discardReqId = new RequestId("action-discard-1");
                byte[] discardPayloadA = new byte[] { 10, 20, 30 };
                byte[] discardPayloadB = new byte[] { 99, 88, 77 };

                RoundRecoveryState initialRound = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
                IReadOnlyList<TileId> handTiles = initialRound.Tiles.TilesInZone(TileZone.Hand, 0);
                TileId tileToDiscard = handTiles[0];
                TransitionResult discardTransition = initialRound.Tiles.Discard(0, tileToDiscard);
                Assert.True(discardTransition.IsSuccess);
                TilePositionMap tilesAfterDiscard = discardTransition.RequireState();
                var roundAfterDiscard = new RoundRecoveryState(
                    initialRound.Spec,
                    1L,
                    RoundPhase.Discard,
                    0,
                    tilesAfterDiscard,
                    null,
                    null,
                    Array.Empty<RoundEvent>(),
                    checkpoint.StateFingerprint + 1UL);
                byte[] discardStatePayload = RoundRecoveryCodec.Encode(roundAfterDiscard);
                ulong discardFingerprint = checkpoint.StateFingerprint + 1UL;

                // Pre-commit baseline assertions: 0 events, 0 commands, 0 outbox, 0 state version
                Assert.Empty(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Empty(await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                TableTimingCheckpoint timing1 = AdvanceTiming(timing);
                var discardReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    0UL,
                    2L,
                    timing1,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    discardReqId,
                    "DISCARD_TILE",
                    discardPayloadA,
                    nextStatePayload: discardStatePayload,
                    nextStateFingerprint: discardFingerprint);

                // 1a. First commit succeeds
                TableDurableCommitResult discardRes1 = await store.CommitAsync(discardReq1, ct);
                Assert.True(discardRes1.IsCommitted, "disposition=" + discardRes1.Disposition + " reason=" + discardRes1.ReasonCode + " detail=" + discardRes1.Detail);
                Assert.False(discardRes1.IsReplayed);

                TableStateRow stateAfterDiscard = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, stateAfterDiscard.StateVersion.Value);
                Assert.Equal(discardFingerprint, stateAfterDiscard.StateFingerprint);
                Assert.Equal(TablePayloadSha256.Compute(discardStatePayload), stateAfterDiscard.StateHash);
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Single(await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // 1b. Replay with exact same scope + requestId + payload bytes: executed once, IsReplayed = true
                TableDurableCommitResult discardReplay = await store.CommitAsync(discardReq1, ct);
                Assert.True(discardReplay.IsReplayed);
                TableStateRow stateAfterReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, stateAfterReplay.StateVersion.Value);
                Assert.Equal(discardFingerprint, stateAfterReplay.StateFingerprint);
                Assert.Equal(TablePayloadSha256.Compute(discardStatePayload), stateAfterReplay.StateHash);
                // Real values do not duplicate!
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Single(await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // 1c. Conflict with same scope + requestId + DIFFERENT payload bytes: Conflict
                var discardConflictReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    0UL,
                    2L,
                    timing1,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    discardReqId,
                    "DISCARD_TILE",
                    discardPayloadB,
                    nextStatePayload: discardStatePayload,
                    nextStateFingerprint: discardFingerprint);
                TableDurableCommitResult discardConflictRes = await store.CommitAsync(discardConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, discardConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, discardConflictRes.ReasonCode);
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Single(await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // --- 2. Three Cheats: TileMark, SleeveBloom, BeamSwap ---
                // 2A. Cheat 1: TileMark (落汗)
                var markReqId = new RequestId("cheat-mark-1");
                byte[] markPayloadA = new byte[] { 31, 32, 33 };
                byte[] markPayloadB = new byte[] { 34, 35, 36 };

                TableTimingCheckpoint timing2 = AdvanceTiming(timing1);
                var markReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    1UL,
                    3L,
                    timing2,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    markReqId,
                    "CHEAT_TILE_MARK",
                    markPayloadA,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 1L,
                    nextCheatFingerprint: 101UL);

                TableDurableCommitResult markRes1 = await store.CommitAsync(markReq1, ct);
                Assert.True(markRes1.IsCommitted);
                TableStateRow stateAfterMark = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2UL, stateAfterMark.StateVersion.Value);
                Assert.Equal(1L, stateAfterMark.CheatVersion);
                Assert.Equal(101UL, stateAfterMark.CheatFingerprint);
                Assert.True(stateAfterMark.CheatBound);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(2, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                TableDurableCommitResult markReplay = await store.CommitAsync(markReq1, ct);
                Assert.True(markReplay.IsReplayed);
                TableStateRow stateAfterMarkReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2UL, stateAfterMarkReplay.StateVersion.Value);
                Assert.Equal(1L, stateAfterMarkReplay.CheatVersion);
                Assert.Equal(101UL, stateAfterMarkReplay.CheatFingerprint);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(2, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                var markConflictReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    1UL,
                    3L,
                    timing2,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    markReqId,
                    "CHEAT_TILE_MARK",
                    markPayloadB,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 1L,
                    nextCheatFingerprint: 101UL);
                TableDurableCommitResult markConflictRes = await store.CommitAsync(markConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, markConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, markConflictRes.ReasonCode);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(2, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // 2B. Cheat 2: SleeveBloom (袖里藏花)
                var sleeveReqId = new RequestId("cheat-sleeve-1");
                byte[] sleevePayloadA = new byte[] { 40, 50, 60 };
                byte[] sleevePayloadB = new byte[] { 44, 55, 66 };

                TableTimingCheckpoint timing3 = AdvanceTiming(timing2);
                var sleeveReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    2UL,
                    4L,
                    timing3,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    sleeveReqId,
                    "CHEAT_SLEEVE_BLOOM",
                    sleevePayloadA,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 2L,
                    nextCheatFingerprint: 102UL);

                TableDurableCommitResult sleeveRes1 = await store.CommitAsync(sleeveReq1, ct);
                Assert.True(sleeveRes1.IsCommitted);
                TableStateRow stateAfterSleeve = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3UL, stateAfterSleeve.StateVersion.Value);
                Assert.Equal(2L, stateAfterSleeve.CheatVersion);
                Assert.Equal(102UL, stateAfterSleeve.CheatFingerprint);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(3, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                TableDurableCommitResult sleeveReplay = await store.CommitAsync(sleeveReq1, ct);
                Assert.True(sleeveReplay.IsReplayed);
                TableStateRow stateAfterSleeveReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3UL, stateAfterSleeveReplay.StateVersion.Value);
                Assert.Equal(2L, stateAfterSleeveReplay.CheatVersion);
                Assert.Equal(102UL, stateAfterSleeveReplay.CheatFingerprint);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(3, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                var sleeveConflictReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    2UL,
                    4L,
                    timing3,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    sleeveReqId,
                    "CHEAT_SLEEVE_BLOOM",
                    sleevePayloadB,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 2L,
                    nextCheatFingerprint: 102UL);
                TableDurableCommitResult sleeveConflictRes = await store.CommitAsync(sleeveConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, sleeveConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, sleeveConflictRes.ReasonCode);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(3, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // 2C. Cheat 3: BeamSwap (偷梁换柱)
                var beamReqId = new RequestId("cheat-beam-1");
                byte[] beamPayloadA = new byte[] { 51, 52, 53 };
                byte[] beamPayloadB = new byte[] { 54, 55, 56 };

                TableTimingCheckpoint timing4 = AdvanceTiming(timing3);
                var beamReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    3UL,
                    5L,
                    timing4,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    beamReqId,
                    "CHEAT_BEAM_SWAP",
                    beamPayloadA,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 3L,
                    nextCheatFingerprint: 103UL);

                TableDurableCommitResult beamRes1 = await store.CommitAsync(beamReq1, ct);
                Assert.True(beamRes1.IsCommitted);
                TableStateRow stateAfterBeam = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(4UL, stateAfterBeam.StateVersion.Value);
                Assert.Equal(3L, stateAfterBeam.CheatVersion);
                Assert.Equal(103UL, stateAfterBeam.CheatFingerprint);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(4, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                TableDurableCommitResult beamReplay = await store.CommitAsync(beamReq1, ct);
                Assert.True(beamReplay.IsReplayed);
                TableStateRow stateAfterBeamReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(4UL, stateAfterBeamReplay.StateVersion.Value);
                Assert.Equal(3L, stateAfterBeamReplay.CheatVersion);
                Assert.Equal(103UL, stateAfterBeamReplay.CheatFingerprint);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(4, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                var beamConflictReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    3UL,
                    5L,
                    timing4,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    beamReqId,
                    "CHEAT_BEAM_SWAP",
                    beamPayloadB,
                    eventType: RoundEventType.CheatApplied,
                    nextCheatVersion: 3L,
                    nextCheatFingerprint: 103UL);
                TableDurableCommitResult beamConflictRes = await store.CommitAsync(beamConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, beamConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, beamConflictRes.ReasonCode);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(4, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // --- 3. Settlement Action: Tsumo with Outbox ---
                var settleReqId = new RequestId("settle-tsumo-1");
                byte[] settlePayloadA = new byte[] { 70, 80, 90 };
                byte[] settlePayloadB = new byte[] { 77, 88, 99 };
                byte[] outboxPayload = new byte[] { 170, 180, 190 };
                var settlementOutbox = new List<TableSettlementOutboxRecord>
                {
                    new TableSettlementOutboxRecord(
                        "match-settle-1",
                        1,
                        outboxPayload,
                        TablePayloadSha256.Compute(outboxPayload),
                        nowUtc)
                };

                TableTimingCheckpoint timing5 = AdvanceTiming(timing4);
                var settleReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    4UL,
                    6L,
                    timing5,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    settleReqId,
                    "DECLARE_TSUMO",
                    settlePayloadA,
                    eventType: RoundEventType.TsumoWin,
                    nextCheatVersion: 3L,
                    nextCheatFingerprint: 103UL,
                    terminal: true,
                    outbox: settlementOutbox);

                // 3a. First commit succeeds
                TableDurableCommitResult settleRes1 = await store.CommitAsync(settleReq1, ct);
                Assert.True(settleRes1.IsCommitted);
                TableStateRow stateAfterSettle = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, stateAfterSettle.StateVersion.Value);

                TableSettlementOutboxRecord? initialOutbox = await store.TryGetAsync("match-settle-1", 1, ct);
                Assert.NotNull(initialOutbox);
                Assert.Equal(TablePayloadSha256.Compute(outboxPayload), initialOutbox!.PayloadSha256);
                Assert.Single(await store.ListUnpublishedAsync(ct));
                Assert.Equal(5, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(5, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // 3b. Replay succeeds without duplicate side effects
                TableDurableCommitResult settleReplay = await store.CommitAsync(settleReq1, ct);
                Assert.True(settleReplay.IsReplayed);
                TableStateRow stateAfterSettleReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, stateAfterSettleReplay.StateVersion.Value);
                Assert.Single(await store.ListUnpublishedAsync(ct));
                Assert.Equal(5, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(5, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // 3c. Conflict with different bytes
                var settleConflictReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    4UL,
                    6L,
                    timing5,
                    checkpoint,
                    accountId,
                    validScope.ScopeId,
                    settleReqId,
                    "DECLARE_TSUMO",
                    settlePayloadB,
                    eventType: RoundEventType.TsumoWin,
                    nextCheatVersion: 3L,
                    nextCheatFingerprint: 103UL,
                    terminal: true,
                    outbox: settlementOutbox);
                TableDurableCommitResult settleConflictRes = await store.CommitAsync(settleConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, settleConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, settleConflictRes.ReasonCode);
                Assert.Single(await store.ListUnpublishedAsync(ct));
                Assert.Equal(5, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(5, (await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct)).Count);

                // --- 4. Expired Scope Rejection ---
                var expiredScope = store.IssueScope(
                    accountId,
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    issuedAtUtcMs: nowUtc - 20_000UL,
                    expiresAtUtcMs: nowUtc - 10_000UL,
                    scopeId: "scope-expired-1");

                TableTimingCheckpoint timing6 = AdvanceTiming(timing5);
                var expiredReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    5UL,
                    7L,
                    timing6,
                    checkpoint,
                    accountId,
                    expiredScope.ScopeId,
                    new RequestId("req-expired-1"),
                    "DISCARD_TILE",
                    new byte[] { 1, 2, 3 },
                    createdAtUtcMs: nowUtc - 20_000UL,
                    expiresAtUtcMs: nowUtc - 10_000UL);

                TableDurableCommitResult expiredRes = await store.CommitAsync(expiredReq, ct);
                Assert.Equal(TableCommitDisposition.ScopeRejected, expiredRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestScopeExpired, expiredRes.ReasonCode);
                TableStateRow stateAfterExpiredReject = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, stateAfterExpiredReject.StateVersion.Value);
            }

            // --- 5. Cold Reopen Matrix (Blocker 1 & 2 Regressions) ---
            // 5a. First cold reopen: hydrates all 5 operations under same scope without "Scope id already exists"
            using (LocalTableStore cold1 = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow cold1State = (await cold1.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, cold1State.StateVersion.Value);
                Assert.Equal(6L, cold1State.TimingVersion);
                Assert.Equal(3L, cold1State.CheatVersion);
                Assert.Equal(103UL, cold1State.CheatFingerprint);
                Assert.True(cold1State.CheatBound);

                // All 5 receipts exist and match original fields
                Assert.NotNull(await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct));
                Assert.NotNull(await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-mark-1"), ct));
                Assert.NotNull(await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-sleeve-1"), ct));
                Assert.NotNull(await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-beam-1"), ct));
                Assert.NotNull(await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct));

                // Outbox persisted
                Assert.NotNull(await cold1.TryGetAsync("match-settle-1", 1, ct));
                Assert.Single(await cold1.ListUnpublishedAsync(ct));

                // Replay re-commit returns IsReplayed without advancing state
                var replayDiscard = await cold1.CommitAsync(
                    BuildPlayerCommitRequest(
                        P41Harness.RoomId, "owner-local", cold1.LeaseEpoch, 0UL, 2L,
                        AdvanceTiming(TableTimingCheckpointCodec.Decode((await cold1.LoadCheckpointAsync(P41Harness.RoomId, ct))!.TimingPayload)),
                        (await cold1.LoadCheckpointAsync(P41Harness.RoomId, ct))!,
                        "player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"),
                        "DISCARD_TILE", new byte[] { 10, 20, 30 }),
                    ct);
                Assert.True(replayDiscard.IsReplayed);
                TableStateRow stateAfterColdReplay = (await cold1.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, stateAfterColdReplay.StateVersion.Value);
            }

            // 5b. Second consecutive cold reopen verifies durable stability
            using (LocalTableStore cold2 = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow cold2State = (await cold2.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, cold2State.StateVersion.Value);
                Assert.NotNull(await cold2.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct));
                Assert.NotNull(await cold2.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct));
            }

            // 5c. Advance clock past scope expiry: historical receipts remain queryable, but NEW requests are rejected
            harness.Clock.UtcNowMs += 3_600_000L; // 1 hour later (well past 10-minute validity)
            using (LocalTableStore cold3 = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                // Existing receipts are still intact and re-committing identical request returns IsReplayed
                TableDurableReceipt? historical = await cold3.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct);
                Assert.NotNull(historical);

                var historicalReplay = await cold3.CommitAsync(
                    BuildPlayerCommitRequest(
                        P41Harness.RoomId, "owner-local", cold3.LeaseEpoch, 0UL, 2L,
                        AdvanceTiming(TableTimingCheckpointCodec.Decode((await cold3.LoadCheckpointAsync(P41Harness.RoomId, ct))!.TimingPayload)),
                        (await cold3.LoadCheckpointAsync(P41Harness.RoomId, ct))!,
                        "player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"),
                        "DISCARD_TILE", new byte[] { 10, 20, 30 }),
                    ct);
                Assert.True(historicalReplay.IsReplayed);

                // A NEW request with this expired scope is strictly rejected (NOT revitalized by replay)
                TableCheckpoint cp3 = (await cold3.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timingCold3 = AdvanceTiming(TableTimingCheckpointCodec.Decode(cp3.TimingPayload));
                var newReqExpired = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    cold3.LeaseEpoch,
                    5UL,
                    7L,
                    timingCold3,
                    cp3,
                    "player-account-0",
                    new RequestScopeId("scope-valid-1"),
                    new RequestId("brand-new-req-after-expiry"),
                    "DISCARD_TILE",
                    new byte[] { 1, 2, 3 },
                    createdAtUtcMs: nowUtc - 10_000UL,
                    expiresAtUtcMs: nowUtc + 600_000UL);

                TableDurableCommitResult expiredAtReopenRes = await cold3.CommitAsync(newReqExpired, ct);
                Assert.Equal(TableCommitDisposition.ScopeRejected, expiredAtReopenRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestScopeExpired, expiredAtReopenRes.ReasonCode);

                TableStateRow finalState = (await cold3.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, finalState.StateVersion.Value);
            }
        }

        private static async Task<LocalTableStore> OpenFreshStoreAsync(
            P41Harness harness,
            ILocalFileSystem fs,
            CancellationToken cancellationToken)
        {
            LocalTableStore store = await LocalTableStore.OpenAsync(
                harness.Options,
                harness.Clock,
                fs,
                cancellationToken);
            var facts = new LocalSnapshotFacts(harness.Options.Environment!, harness.Clock);
            var round = facts.CreateInitialRound(harness.Options);
            TableCheckpoint checkpoint = facts.CreateInitialCheckpoint(harness.Options, round);
            try
            {
                await store.CreateRoomAsync(checkpoint, cancellationToken);
                return store;
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private static async Task<TableCheckpoint> BuildNextCheckpointAsync(LocalTableStore store, ManualClock? clock = null)
        {
            TableCheckpoint initial = await store.LoadCheckpointAsync(
                P41Harness.RoomId,
                TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("missing checkpoint");
            ulong utcMs = clock != null ? (ulong)clock.UtcNowMs : initial.CreatedAtUtcMs;
            long monoMs = clock != null ? clock.MonotonicMillis : initial.CapturedMonotonicMillis;
            TableTimingCheckpoint initialTiming = TableTimingCheckpointCodec.Decode(initial.TimingPayload);
            var nextTiming = new TableTimingCheckpoint(
                initialTiming.RoundId,
                initialTiming.SeatReservesMillis,
                initialTiming.Decisions,
                utcMs,
                monoMs,
                initialTiming.BootId);
            byte[] nextTimingPayload = TableTimingCheckpointCodec.Encode(nextTiming);
            return new TableCheckpoint(
                initial.RoomId,
                initial.StateVersion,
                initial.LastEventSeq,
                initial.StateFingerprint,
                initial.RulesetVersion,
                initial.StatePayload,
                initial.StatePayloadSha256,
                initial.ControlVersion,
                initial.ControlPayload,
                initial.ControlPayloadSha256,
                nextTimingPayload,
                TablePayloadSha256.Compute(nextTimingPayload),
                initial.CheatPayload,
                initial.CheatPayloadSha256,
                initial.CommittedFactsPayload,
                initial.CommittedFactsPayloadSha256,
                utcMs,
                initial.BootId,
                initial.SchemaVersion,
                initial.TimingVersion + 1L,
                monoMs,
                initial.CommittedHistoryPayload,
                initial.CommittedHistoryPayloadSha256,
                initial.CommittedCommandBindingsPayload,
                initial.CommittedCommandBindingsPayloadSha256);
        }

        private static async Task<TableDurableCommitResult> SaveCheckpointAsync(
            LocalTableStore store,
            TableCheckpoint checkpoint,
            long expectedTiming,
            CancellationToken cancellationToken = default)
        {
            await store.AcquireAsync(
                P41Harness.RoomId,
                "owner-local",
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
            var fence = new TableCheckpointFence(
                P41Harness.RoomId,
                "owner-local",
                store.LeaseEpoch,
                new StateVersion(0),
                0L,
                expectedTiming);
            return await store.SaveCheckpointAsync(checkpoint, fence, cancellationToken);
        }

        private static TableDurableCommitRequest BuildInternalCommitRequest(
            string roomId,
            string owner,
            OwnerEpoch epoch,
            ulong expectedStateVersion,
            long timingVersion,
            TableTimingCheckpoint timing,
            TableCheckpoint checkpoint)
        {
            var events = new List<TableEventRecord>
            {
                new TableEventRecord(
                    new EventSeq(expectedStateVersion + 1UL),
                    new StateVersion(expectedStateVersion + 1UL),
                    0,
                    RoundEventType.TileDiscarded,
                    0,
                    new TileId(0),
                    "internal-discard",
                    1,
                    new byte[] { 1, 2, 3 })
            };
            var journal = new TableCommandJournalRecord(
                new StateVersion(expectedStateVersion + 1UL),
                new StateVersion(expectedStateVersion),
                new RequestId("internal-req-" + (expectedStateVersion + 1UL)),
                "local-seat0",
                new RequestScopeId("scope-internal"),
                "INTERNAL_COMMAND",
                TablePayloadSha256.Compute(new byte[] { 1, 2, 3 }),
                new byte[] { 1, 2, 3 });

            return new TableDurableCommitRequest(
                roomId,
                owner,
                epoch,
                new StateVersion(expectedStateVersion),
                receipt: null,
                internalCommand: true,
                events: events,
                commandJournal: journal,
                outbox: null,
                checkpoint: null,
                nextStatePayload: checkpoint.StatePayload,
                nextStateFingerprint: checkpoint.StateFingerprint + 1UL,
                terminal: false,
                timing: timing,
                timingVersion: timingVersion,
                committedFactsPayload: checkpoint.CommittedFactsPayload,
                committedFactsPayloadSha256: checkpoint.CommittedFactsPayloadSha256,
                nextCheatVersion: 0L,
                nextCheatFingerprint: 0UL);
        }

        private static TableDurableCommitRequest BuildPlayerCommitRequest(
            string roomId,
            string owner,
            OwnerEpoch epoch,
            ulong expectedStateVersion,
            long timingVersion,
            TableTimingCheckpoint timing,
            TableCheckpoint checkpoint,
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            string api,
            byte[] businessPayload,
            RoundEventType eventType = RoundEventType.TileDiscarded,
            long nextCheatVersion = 0L,
            ulong nextCheatFingerprint = 0UL,
            bool terminal = false,
            IReadOnlyList<TableSettlementOutboxRecord>? outbox = null,
            ulong createdAtUtcMs = 1_700_000_000_000UL,
            ulong expiresAtUtcMs = 1_700_000_600_000UL,
            byte[]? nextStatePayload = null,
            ulong? nextStateFingerprint = null)
        {
            var receiptDigest = TablePayloadSha256.Compute(businessPayload);
            var receipt = new TableDurableReceipt(
                accountId,
                scopeId,
                requestId,
                api,
                receiptDigest,
                Receipt.Applied(
                    new StateVersion(expectedStateVersion),
                    new StateVersion(expectedStateVersion + 1UL),
                    new EventSeq(expectedStateVersion + 1UL)),
                null,
                createdAtUtcMs,
                expiresAtUtcMs);
            var events = new List<TableEventRecord>
            {
                new TableEventRecord(
                    new EventSeq(expectedStateVersion + 1UL),
                    new StateVersion(expectedStateVersion + 1UL),
                    0,
                    eventType,
                    0,
                    new TileId((int)(expectedStateVersion % 136)),
                    api,
                    1,
                    businessPayload)
            };
            var journal = new TableCommandJournalRecord(
                new StateVersion(expectedStateVersion + 1UL),
                new StateVersion(expectedStateVersion),
                requestId,
                accountId,
                scopeId,
                api,
                receiptDigest,
                businessPayload);

            byte[] effectiveStatePayload = nextStatePayload ?? checkpoint.StatePayload;
            ulong effectiveFingerprint = nextStateFingerprint ?? (checkpoint.StateFingerprint + expectedStateVersion + 1UL);

            return new TableDurableCommitRequest(
                roomId,
                owner,
                epoch,
                new StateVersion(expectedStateVersion),
                receipt: receipt,
                internalCommand: false,
                events: events,
                commandJournal: journal,
                outbox: outbox,
                checkpoint: null,
                nextStatePayload: effectiveStatePayload,
                nextStateFingerprint: effectiveFingerprint,
                terminal: terminal,
                timing: timing,
                timingVersion: timingVersion,
                committedFactsPayload: checkpoint.CommittedFactsPayload,
                committedFactsPayloadSha256: checkpoint.CommittedFactsPayloadSha256,
                nextCheatVersion: nextCheatVersion,
                nextCheatFingerprint: nextCheatFingerprint);
        }

        private static TableTimingCheckpoint AdvanceTiming(TableTimingCheckpoint initial, ManualClock? clock = null)
        {
            ulong utcMs = clock != null ? (ulong)clock.UtcNowMs : initial.CapturedAtUtcMs + 1000UL;
            long monoMs = clock != null ? clock.MonotonicMillis : initial.CapturedMonotonicMillis + 1000L;
            return new TableTimingCheckpoint(
                initial.RoundId,
                initial.SeatReservesMillis,
                initial.Decisions,
                utcMs,
                monoMs,
                initial.BootId);
        }

        private static (TableControlCommitRequest Request, TableCheckpoint Candidate) BuildControlRequest(
            LocalTableStore store,
            TableCheckpoint checkpoint,
            long currentControlVersion,
            long expectedTimingVersion)
        {
            long nextControl = currentControlVersion + 1L;
            long nextTiming = expectedTimingVersion + 1L;
            TableTimingCheckpoint initialTiming = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
            string roundId = string.IsNullOrEmpty(initialTiming.RoundId) ? "round-1" : initialTiming.RoundId;
            var change = new TableControlEvent(
                nextControl,
                roundId,
                0,
                TableControlChangeKind.ExplicitTakeover,
                null, null, null, null, null, null, null,
                1000L, 1000L, "control-policy");
            TableControlCheckpoint decoded = TableControlCheckpointCodec.Decode(checkpoint.ControlPayload);
            var nextControlCp = new TableControlCheckpoint(decoded.Snapshot, nextControl);
            byte[] controlPayload = TableControlCheckpointCodec.Encode(nextControlCp);
            var candidate = new TableCheckpoint(
                checkpoint.RoomId,
                checkpoint.StateVersion,
                checkpoint.LastEventSeq,
                checkpoint.StateFingerprint,
                checkpoint.RulesetVersion,
                checkpoint.StatePayload,
                checkpoint.StatePayloadSha256,
                nextControl,
                controlPayload,
                TablePayloadSha256.Compute(controlPayload),
                checkpoint.TimingPayload,
                checkpoint.TimingPayloadSha256,
                checkpoint.CheatPayload,
                checkpoint.CheatPayloadSha256,
                checkpoint.CommittedFactsPayload,
                checkpoint.CommittedFactsPayloadSha256,
                checkpoint.CreatedAtUtcMs,
                checkpoint.BootId,
                checkpoint.SchemaVersion,
                nextTiming,
                checkpoint.CapturedMonotonicMillis,
                checkpoint.CommittedHistoryPayload,
                checkpoint.CommittedHistoryPayloadSha256,
                checkpoint.CommittedCommandBindingsPayload,
                checkpoint.CommittedCommandBindingsPayloadSha256);
            var request = new TableControlCommitRequest(
                checkpoint.RoomId,
                store.LeaseEpoch,
                currentControlVersion,
                nextControl,
                checkpoint.StateVersion,
                roundId,
                change,
                "owner-local");
            return (request, candidate);
        }
    }
}
