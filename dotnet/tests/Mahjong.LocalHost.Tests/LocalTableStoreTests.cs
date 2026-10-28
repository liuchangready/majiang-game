using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
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

                // Initial payload/hash negative controls verified at checkpoint construction boundary
                byte[] tamperedInitState = (byte[])checkpoint.StatePayload.Clone();
                tamperedInitState[tamperedInitState.Length - 1] ^= 0x01;
                Assert.Throws<ArgumentException>(() => new TableCheckpoint(
                    checkpoint.RoomId, checkpoint.StateVersion, checkpoint.LastEventSeq,
                    checkpoint.StateFingerprint, checkpoint.RulesetVersion,
                    tamperedInitState, checkpoint.StatePayloadSha256,
                    checkpoint.ControlVersion, checkpoint.ControlPayload, checkpoint.ControlPayloadSha256,
                    checkpoint.TimingPayload, checkpoint.TimingPayloadSha256,
                    checkpoint.CheatPayload, checkpoint.CheatPayloadSha256,
                    checkpoint.CommittedFactsPayload, checkpoint.CommittedFactsPayloadSha256,
                    checkpoint.CreatedAtUtcMs, checkpoint.BootId));

                byte[] tamperedInitControl = (byte[])checkpoint.ControlPayload.Clone();
                tamperedInitControl[tamperedInitControl.Length - 1] ^= 0x01;
                Assert.Throws<ArgumentException>(() => new TableCheckpoint(
                    checkpoint.RoomId, checkpoint.StateVersion, checkpoint.LastEventSeq,
                    checkpoint.StateFingerprint, checkpoint.RulesetVersion,
                    checkpoint.StatePayload, checkpoint.StatePayloadSha256,
                    checkpoint.ControlVersion, tamperedInitControl, checkpoint.ControlPayloadSha256,
                    checkpoint.TimingPayload, checkpoint.TimingPayloadSha256,
                    checkpoint.CheatPayload, checkpoint.CheatPayloadSha256,
                    checkpoint.CommittedFactsPayload, checkpoint.CommittedFactsPayloadSha256,
                    checkpoint.CreatedAtUtcMs, checkpoint.BootId));

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

                // 1) Owner fence negative: wrong owner instance
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

                // 1b) Owner fence negative: wrong owner epoch
                var wrongOwnerEpochFence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    new OwnerEpoch(store.LeaseEpoch.Value + 99UL),
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L);
                TableDurableCommitResult wrongEpochRes = await store.SaveCheckpointAsync(checkpoint, wrongOwnerEpochFence, ct);
                Assert.Equal(TableCommitDisposition.StaleOwner, wrongEpochRes.Disposition);
                Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, wrongEpochRes.ReasonCode);

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

                // 5b) Committed facts payload hash mismatch rejected at checkpoint construction boundary
                byte[] tamperedFacts = (byte[])checkpoint.CommittedFactsPayload!.Clone();
                tamperedFacts[tamperedFacts.Length - 1] ^= 0x01;
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
                    checkpoint.TimingPayload,
                    checkpoint.TimingPayloadSha256,
                    checkpoint.CheatPayload,
                    checkpoint.CheatPayloadSha256,
                    tamperedFacts,
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

                // Verify negative attempts have zero side effects on store state
                TableStateRow preSaveState = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(checkpoint.StateVersion, preSaveState.StateVersion);
                Assert.Equal(checkpoint.ControlVersion, preSaveState.ControlVersion);
                Assert.Equal(1L, preSaveState.TimingVersion);
                Assert.Equal(checkpoint.TimingVersion - 1L, preSaveState.TimingVersion);
                Assert.Equal(checkpoint.StatePayloadSha256, preSaveState.StateHash);
                Assert.Equal(checkpoint.StateFingerprint, preSaveState.StateFingerprint);
                Assert.Empty(await store.LoadCommandsAfterAsync(P41Harness.RoomId, checkpoint.StateVersion, ct));
                Assert.Empty(await store.LoadEventsAfterAsync(P41Harness.RoomId, checkpoint.LastEventSeq, ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

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
        public async Task RejectedReceiptCommitDoesNotRegressStateOrTimingVersion()
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
                var append = new TableTimingAppendRequest(
                    checkpoint.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    1L,
                    AdvanceTiming(timing));
                Assert.True((await store.AppendTimingAsync(append, ct)).IsCommitted);

                TableScopeRecord scope = store.IssueScope(
                    "acct-rejected",
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    1_700_000_000_000UL,
                    1_700_000_600_000UL,
                    "scope-rejected");
                byte[] payload = new byte[] { 1, 2, 3 };
                var receipt = new TableDurableReceipt(
                    scope.AccountId,
                    scope.ScopeId,
                    new RequestId("rejected-receipt"),
                    "TEST_REJECT",
                    TablePayloadSha256.Compute(payload),
                    Receipt.Rejected(new StateVersion(0), "TEST_REJECTED"),
                    "rejected",
                    1_700_000_000_000UL,
                    1_700_000_600_000UL);
                var request = new TableDurableCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    new StateVersion(0),
                    receipt,
                    internalCommand: false,
                    events: null,
                    commandJournal: null,
                    outbox: null,
                    checkpoint: null,
                    nextStatePayload: null,
                    nextStateFingerprint: 0,
                    terminal: false,
                    timing: null,
                    timingVersion: 0);
                TableDurableCommitResult committed = await store.CommitAsync(request, ct);
                Assert.True(committed.IsCommitted, committed.ReasonCode + " " + committed.Detail);

                TableStateRow after = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(0UL, after.StateVersion.Value);
                Assert.Equal(2L, after.TimingVersion);
            }

            using (LocalTableStore reopened = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow after = (await reopened.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(0UL, after.StateVersion.Value);
                Assert.Equal(2L, after.TimingVersion);
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

            TableDurableCommitRequest? discardReq1 = null;
            TableDurableCommitRequest? discardConflictReq = null;
            TableDurableCommitRequest? markReq1 = null;
            TableDurableCommitRequest? sleeveReq1 = null;
            TableDurableCommitRequest? beamReq1 = null;
            TableDurableCommitRequest? settleReq1 = null;
            CheatState? cheat3 = null;
            byte[]? discardStatePayload = null;
            ulong discardFingerprint = 0UL;
            TilePositionMap? tilesAfterBeam = null;
            int targetWallIndex = -1;
            RoundSpec? initialSpec = null;
            TileId tileToDiscard = default;
            byte[] outboxPayload = new byte[] { 170, 180, 190 };
            TileId handTileToSwap = default;
            byte[]? discardPayloadA = null;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                var facts = new LocalSnapshotFacts(harness.Options.Environment!, harness.Clock);

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

                RoundRecoveryState initialRound = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
                initialSpec = initialRound.Spec;
                IReadOnlyList<TileId> handTiles = initialRound.Tiles.TilesInZone(TileZone.Hand, 0);
                tileToDiscard = handTiles[0];
                TransitionResult discardTransition = initialRound.Tiles.Discard(0, tileToDiscard);
                Assert.True(discardTransition.IsSuccess);
                TilePositionMap tilesAfterDiscard = discardTransition.RequireState();

                // Canonical business payloads from CommandPayloadFactory
                BusinessPayload pDiscard = CommandPayloadFactory.SubmitAction(initialSpec.RoundId, "dec-discard-1", 0UL, ProtocolActionType.DiscardTile, tileIds: new uint[] { (uint)tileToDiscard.Value });
                discardPayloadA = pDiscard.CopyBytes();
                byte[] discardPayloadB = (byte[])discardPayloadA.Clone();
                discardPayloadB[^1] ^= 0xFF; // Strict conflicting byte

                var roundEvent1 = new RoundEvent(RoundEventType.TileDiscarded, 0, 0, tileToDiscard, "DISCARD_TILE");
                RoundStateMachine round1 = CreateRoundWithEvents(tilesAfterDiscard, initialSpec, new[] { roundEvent1 }, version: 1L, phase: RoundPhase.Discard);
                discardFingerprint = round1.SourceFingerprint;
                discardStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round1));

                var event1 = new TableEventRecord(
                    new EventSeq(1UL), new StateVersion(1UL), 0,
                    RoundEventType.TileDiscarded, 0, tileToDiscard, "DISCARD_TILE", 1,
                    TableEventFactCodec.Encode(new StateVersion(1UL), 0, discardFingerprint));
                var binding1 = new TableCommandBinding(1UL, 1, discardFingerprint);

                // Pre-commit baseline assertions: 0 events, 0 commands, 0 outbox, 0 state version
                Assert.Empty(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Empty(await store.LoadCommandsAfterAsync(P41Harness.RoomId, new StateVersion(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing1 = AdvanceTiming(timing, harness.Clock);
                TableProjectionContextData factsData1 = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round1,
                    new StateVersion(1UL),
                    new EventSeq(1UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timing1.CapturedMonotonicMillis,
                    (long)timing1.CapturedAtUtcMs,
                    new[] { event1 }));
                byte[] factsPayload1 = TableProjectionFactsCodec.Encode(factsData1);

                CheatState cheat0 = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload!).RestoreCheatState();
                var cp1 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round1,
                    new StateVersion(1UL),
                    new EventSeq(1UL),
                    TableControlCheckpointCodec.Decode(checkpoint.ControlPayload),
                    timing1,
                    harness.Options.RulesetVersion,
                    checkpoint.BootId,
                    timing1.CapturedAtUtcMs,
                    TableCheatLedgerCheckpoint.FromCheatState(cheat0),
                    factsPayload1,
                    2L,
                    timing1.CapturedMonotonicMillis,
                    new[] { event1 },
                    new[] { binding1 });

                byte[] discardJournalBytes = RoundCommandCodec.Encode(RoundCommand.Discard(initialSpec.RoundId, 0, tileToDiscard));

                discardReq1 = BuildPlayerCommitRequest(
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
                    nextStateFingerprint: discardFingerprint,
                    tile: tileToDiscard,
                    commitCheckpoint: cp1,
                    journalPayload: discardJournalBytes);

                // 1a. First commit succeeds and atomically installs cp1
                TableDurableCommitResult discardRes1 = await store.CommitAsync(discardReq1, ct);
                Assert.True(discardRes1.IsCommitted, "disposition=" + discardRes1.Disposition + " reason=" + discardRes1.ReasonCode + " detail=" + discardRes1.Detail);
                Assert.False(discardRes1.IsReplayed);

                TableStateRow stateAfterDiscard = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, stateAfterDiscard.StateVersion.Value);
                Assert.Equal(discardFingerprint, stateAfterDiscard.StateFingerprint);
                Assert.Equal(TablePayloadSha256.Compute(discardStatePayload), stateAfterDiscard.StateHash);
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // 1b. Replay with exact same scope + requestId + payload bytes: executed once, IsReplayed = true
                TableDurableCommitResult discardReplay = await store.CommitAsync(discardReq1, ct);
                Assert.True(discardReplay.IsReplayed);
                TableStateRow stateAfterReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, stateAfterReplay.StateVersion.Value);
                Assert.Equal(discardFingerprint, stateAfterReplay.StateFingerprint);
                Assert.Equal(TablePayloadSha256.Compute(discardStatePayload), stateAfterReplay.StateHash);
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // 1c. Conflict with same scope + requestId + DIFFERENT payload bytes: Conflict
                discardConflictReq = BuildPlayerCommitRequest(
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
                    nextStateFingerprint: discardFingerprint,
                    tile: tileToDiscard,
                    commitCheckpoint: cp1,
                    journalPayload: discardJournalBytes);
                TableDurableCommitResult discardConflictRes = await store.CommitAsync(discardConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, discardConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, discardConflictRes.ReasonCode);
                Assert.Single(await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct));
                Assert.Empty(await store.ListUnpublishedAsync(ct));

                // 1d. Immediate Authoritative Cold Recovery 1 (Normal Discard)
                await AssertIntermediateRecoveryAsync(
                    harness,
                    "step1-discard",
                    expectedStateVersion: 1,
                    expectedRound: round1,
                    expectedTiles: tilesAfterDiscard,
                    expectedSeat0Balance: 120,
                    expectedLedgerEntryCount: 0,
                    assertCheat: null,
                    commitReq: discardReq1,
                    conflictReq: discardConflictReq,
                    expectedOutbox: null,
                    expectedOutboxPayload: null,
                    ct);

                // --- 2. Three Cheats: TileMark, SleeveBloom, BeamSwap with real domain changes ---

                // 2A. Cheat 1: TileMark (落汗) with real 4-tile group and 10 pt ledger deduction
                var markReqId = new RequestId("cheat-mark-1");
                BusinessPayload pMarks = CommandPayloadFactory.SubmitTileMark(initialSpec.RoundId, "dec-mark-1", 0UL, new uint[] { 10, 11, 12, 13 });
                byte[] markPayloadA = pMarks.CopyBytes();
                byte[] markPayloadB = (byte[])markPayloadA.Clone();
                markPayloadB[^1] ^= 0xFF;

                var group1 = new TileMarkGroup("group-1", 0, new[] { new TileId(10), new TileId(11), new TileId(12), new TileId(13) }, initialRound.Spec.RoundId, 1);
                var marks1 = TileMarkState.Restore(1L, 1, new[] { initialRound.Spec.RoundId }, Array.Empty<string>(), new[] { group1 });
                CheatLedgerApplyResult ledgerRes1 = cheat0.Ledger.Apply("entry-mark-1", 0, -10, "cheat:tile-mark");
                Assert.True(ledgerRes1.Success);
                CheatLedger ledger1 = ledgerRes1.Ledger!;
                Assert.Equal(110, ledger1.BalanceOf(0));
                var cheat1 = new CheatState(ledger1, marks1, cheat0.SleeveBloom, cheat0.BeamSwap);

                var roundEvent2 = new RoundEvent(RoundEventType.CheatApplied, 1, 0, new TileId(1), "CHEAT_TILE_MARK");
                RoundStateMachine round2 = CreateRoundWithEvents(tilesAfterDiscard, initialSpec, new[] { roundEvent1, roundEvent2 }, version: 2L, phase: RoundPhase.CheatWindow);
                byte[] markStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round2));
                ulong markFingerprint = round2.SourceFingerprint;

                var event2 = new TableEventRecord(
                    new EventSeq(2UL), new StateVersion(2UL), 0,
                    RoundEventType.CheatApplied, 0, new TileId(1), "CHEAT_TILE_MARK", 1,
                    TableEventFactCodec.Encode(new StateVersion(2UL), 0, markFingerprint));
                var binding2 = new TableCommandBinding(2UL, 1, markFingerprint);

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing2 = AdvanceTiming(timing1, harness.Clock);
                TableProjectionContextData factsData2 = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round2,
                    new StateVersion(2UL),
                    new EventSeq(2UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timing2.CapturedMonotonicMillis,
                    (long)timing2.CapturedAtUtcMs,
                    new[] { event1, event2 }));
                byte[] factsPayload2 = TableProjectionFactsCodec.Encode(factsData2);

                var cp2 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round2,
                    new StateVersion(2UL),
                    new EventSeq(2UL),
                    TableControlCheckpointCodec.Decode(checkpoint.ControlPayload),
                    timing2,
                    harness.Options.RulesetVersion,
                    checkpoint.BootId,
                    timing2.CapturedAtUtcMs,
                    TableCheatLedgerCheckpoint.FromCheatState(cheat1),
                    factsPayload2,
                    3L,
                    timing2.CapturedMonotonicMillis,
                    new[] { event1, event2 },
                    new[] { binding1, binding2 });

                markReq1 = BuildPlayerCommitRequest(
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
                    nextCheatVersion: cheat1.Version,
                    nextCheatFingerprint: cheat1.Fingerprint,
                    nextStatePayload: markStatePayload,
                    nextStateFingerprint: markFingerprint,
                    commitCheckpoint: cp2);

                TableDurableCommitResult markRes1 = await store.CommitAsync(markReq1, ct);
                Assert.True(markRes1.IsCommitted);
                TableStateRow stateAfterMark = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2UL, stateAfterMark.StateVersion.Value);
                Assert.Equal(cheat1.Version, stateAfterMark.CheatVersion);
                Assert.Equal(cheat1.Fingerprint, stateAfterMark.CheatFingerprint);
                Assert.True(stateAfterMark.CheatBound);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                TableDurableCommitResult markReplay = await store.CommitAsync(markReq1, ct);
                Assert.True(markReplay.IsReplayed);
                TableStateRow stateAfterMarkReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(2UL, stateAfterMarkReplay.StateVersion.Value);
                Assert.Equal(cheat1.Version, stateAfterMarkReplay.CheatVersion);
                Assert.Equal(cheat1.Fingerprint, stateAfterMarkReplay.CheatFingerprint);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

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
                    nextCheatVersion: cheat1.Version,
                    nextCheatFingerprint: cheat1.Fingerprint,
                    nextStatePayload: markStatePayload,
                    nextStateFingerprint: markFingerprint,
                    commitCheckpoint: cp2);
                TableDurableCommitResult markConflictRes = await store.CommitAsync(markConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, markConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, markConflictRes.ReasonCode);
                Assert.Equal(2, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                // 2A.d. Immediate Authoritative Cold Recovery 2A (TileMark)
                await AssertIntermediateRecoveryAsync(
                    harness,
                    "step2a-mark",
                    expectedStateVersion: 2,
                    expectedRound: round2,
                    expectedTiles: tilesAfterDiscard,
                    expectedSeat0Balance: 110,
                    expectedLedgerEntryCount: 1,
                    assertCheat: ac =>
                    {
                        Assert.Single(ac.TileMarks.Groups);
                        Assert.Equal("group-1", ac.TileMarks.Groups[0].GroupId);
                        Assert.Equal(0, ac.TileMarks.Groups[0].OwnerSeat);
                        Assert.Equal(new[] { new TileId(10), new TileId(11), new TileId(12), new TileId(13) }, ac.TileMarks.Groups[0].TileIds);
                    },
                    commitReq: markReq1,
                    conflictReq: markConflictReq,
                    expectedOutbox: null,
                    expectedOutboxPayload: null,
                    ct);

                // 2B. Cheat 2: SleeveBloom (袖里藏花) with real reservation and 10 pt ledger deduction
                var sleeveReqId = new RequestId("cheat-sleeve-1");
                BusinessPayload pSleeve = CommandPayloadFactory.SubmitSleeveBloom(initialSpec.RoundId, "dec-sleeve-1", 0UL);
                byte[] sleevePayloadA = pSleeve.CopyBytes();
                byte[] sleevePayloadB = (byte[])sleevePayloadA.Clone();
                sleevePayloadB[^1] ^= 0xFF;

                var res2 = new SleeveBloomReservation(0, "sleeve-res-1", new TileId(20), "win-1", 1000L);
                var sleeve2 = SleeveBloomState.Restore(null, Array.Empty<SleeveBloomOffer>(), new[] { res2 }, new string?[4], 1, 0, 1L);
                CheatLedgerApplyResult ledgerRes2 = ledger1.Apply("entry-sleeve-1", 0, -10, "cheat:sleeve-bloom");
                Assert.True(ledgerRes2.Success);
                CheatLedger ledger2 = ledgerRes2.Ledger!;
                Assert.Equal(100, ledger2.BalanceOf(0));
                var cheat2 = new CheatState(ledger2, marks1, sleeve2, cheat0.BeamSwap);

                var roundEvent3 = new RoundEvent(RoundEventType.CheatApplied, 2, 0, new TileId(2), "CHEAT_SLEEVE_BLOOM");
                RoundStateMachine round3 = CreateRoundWithEvents(tilesAfterDiscard, initialSpec, new[] { roundEvent1, roundEvent2, roundEvent3 }, version: 3L, phase: RoundPhase.CheatWindow);
                byte[] sleeveStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round3));
                ulong sleeveFingerprint = round3.SourceFingerprint;

                var event3 = new TableEventRecord(
                    new EventSeq(3UL), new StateVersion(3UL), 0,
                    RoundEventType.CheatApplied, 0, new TileId(2), "CHEAT_SLEEVE_BLOOM", 1,
                    TableEventFactCodec.Encode(new StateVersion(3UL), 0, sleeveFingerprint));
                var binding3 = new TableCommandBinding(3UL, 1, sleeveFingerprint);

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing3 = AdvanceTiming(timing2, harness.Clock);
                TableProjectionContextData factsData3 = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round3,
                    new StateVersion(3UL),
                    new EventSeq(3UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timing3.CapturedMonotonicMillis,
                    (long)timing3.CapturedAtUtcMs,
                    new[] { event1, event2, event3 }));
                byte[] factsPayload3 = TableProjectionFactsCodec.Encode(factsData3);

                var cp3 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round3,
                    new StateVersion(3UL),
                    new EventSeq(3UL),
                    TableControlCheckpointCodec.Decode(checkpoint.ControlPayload),
                    timing3,
                    harness.Options.RulesetVersion,
                    checkpoint.BootId,
                    timing3.CapturedAtUtcMs,
                    TableCheatLedgerCheckpoint.FromCheatState(cheat2),
                    factsPayload3,
                    4L,
                    timing3.CapturedMonotonicMillis,
                    new[] { event1, event2, event3 },
                    new[] { binding1, binding2, binding3 });

                sleeveReq1 = BuildPlayerCommitRequest(
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
                    nextCheatVersion: cheat2.Version,
                    nextCheatFingerprint: cheat2.Fingerprint,
                    nextStatePayload: sleeveStatePayload,
                    nextStateFingerprint: sleeveFingerprint,
                    commitCheckpoint: cp3);

                TableDurableCommitResult sleeveRes1 = await store.CommitAsync(sleeveReq1, ct);
                Assert.True(sleeveRes1.IsCommitted);
                TableStateRow stateAfterSleeve = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3UL, stateAfterSleeve.StateVersion.Value);
                Assert.Equal(cheat2.Version, stateAfterSleeve.CheatVersion);
                Assert.Equal(cheat2.Fingerprint, stateAfterSleeve.CheatFingerprint);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                TableDurableCommitResult sleeveReplay = await store.CommitAsync(sleeveReq1, ct);
                Assert.True(sleeveReplay.IsReplayed);
                TableStateRow stateAfterSleeveReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(3UL, stateAfterSleeveReplay.StateVersion.Value);
                Assert.Equal(cheat2.Version, stateAfterSleeveReplay.CheatVersion);
                Assert.Equal(cheat2.Fingerprint, stateAfterSleeveReplay.CheatFingerprint);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

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
                    nextCheatVersion: cheat2.Version,
                    nextCheatFingerprint: cheat2.Fingerprint,
                    nextStatePayload: sleeveStatePayload,
                    nextStateFingerprint: sleeveFingerprint,
                    commitCheckpoint: cp3);
                TableDurableCommitResult sleeveConflictRes = await store.CommitAsync(sleeveConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, sleeveConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, sleeveConflictRes.ReasonCode);
                Assert.Equal(3, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                // 2B.d. Immediate Authoritative Cold Recovery 2B (SleeveBloom)
                await AssertIntermediateRecoveryAsync(
                    harness,
                    "step2b-sleeve",
                    expectedStateVersion: 3,
                    expectedRound: round3,
                    expectedTiles: tilesAfterDiscard,
                    expectedSeat0Balance: 100,
                    expectedLedgerEntryCount: 2,
                    assertCheat: ac =>
                    {
                        Assert.Single(ac.SleeveBloom.Reservations);
                        Assert.Equal("sleeve-res-1", ac.SleeveBloom.Reservations[0].OfferId);
                        Assert.Equal(new TileId(20), ac.SleeveBloom.Reservations[0].Tile);
                        Assert.Equal(0, ac.SleeveBloom.Reservations[0].Seat);
                    },
                    commitReq: sleeveReq1,
                    conflictReq: sleeveConflictReq,
                    expectedOutbox: null,
                    expectedOutboxPayload: null,
                    ct);

                // 2C. Cheat 3: BeamSwap (偷梁换柱) with real tile swap in physical map, candidate offer, and 10 pt deduction
                var beamReqId = new RequestId("cheat-beam-1");
                BusinessPayload pBeam = CommandPayloadFactory.SubmitBeamSwap(initialSpec.RoundId, "dec-beam-1", 0UL);
                byte[] beamPayloadA = pBeam.CopyBytes();
                byte[] beamPayloadB = (byte[])beamPayloadA.Clone();
                beamPayloadB[^1] ^= 0xFF;

                IReadOnlyList<TileId> handTilesBeforeSwap = tilesAfterDiscard.TilesInZone(TileZone.Hand, 0);
                handTileToSwap = handTilesBeforeSwap[0];
                targetWallIndex = -1;
                for (int w = tilesAfterDiscard.WallPointer; w < tilesAfterDiscard.WallEndExclusive; w++)
                {
                    if (tilesAfterDiscard.TryGetTileAt(TilePosition.Wall(w), out _))
                    {
                        targetWallIndex = w;
                        break;
                    }
                }
                Assert.True(targetWallIndex >= 0);
                TransitionResult beamTransition = tilesAfterDiscard.SwapHandWithWall(0, handTileToSwap, targetWallIndex);
                Assert.True(beamTransition.IsSuccess, beamTransition.Error?.Message ?? "SwapHandWithWall failed");
                tilesAfterBeam = beamTransition.RequireState();

                var cand3 = new BeamSwapCandidate(handTileToSwap, BeamSwapSlot.Wall(targetWallIndex));
                var offer3 = new BeamSwapOffer("beam-offer-1", 0, BeamSwapWindow.Wall, new[] { cand3 }, new[] { new TileId(40) }, new TileId(50), 0);
                var beam3 = BeamSwapState.Restore(offer3, 0, 1L);
                CheatLedgerApplyResult ledgerRes3 = ledger2.Apply("entry-beam-1", 0, -10, "cheat:beam-swap");
                Assert.True(ledgerRes3.Success);
                CheatLedger ledger3 = ledgerRes3.Ledger!;
                Assert.Equal(90, ledger3.BalanceOf(0));
                cheat3 = new CheatState(ledger3, marks1, sleeve2, beam3);

                var roundEvent4 = new RoundEvent(RoundEventType.CheatApplied, 3, 0, new TileId(3), "CHEAT_BEAM_SWAP");
                RoundStateMachine round4 = CreateRoundWithEvents(tilesAfterBeam, initialSpec, new[] { roundEvent1, roundEvent2, roundEvent3, roundEvent4 }, version: 4L, phase: RoundPhase.CheatWindow);
                byte[] beamStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round4));
                ulong beamFingerprint = round4.SourceFingerprint;

                var event4 = new TableEventRecord(
                    new EventSeq(4UL), new StateVersion(4UL), 0,
                    RoundEventType.CheatApplied, 0, new TileId(3), "CHEAT_BEAM_SWAP", 1,
                    TableEventFactCodec.Encode(new StateVersion(4UL), 0, beamFingerprint));
                var binding4 = new TableCommandBinding(4UL, 1, beamFingerprint);

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing4 = AdvanceTiming(timing3, harness.Clock);
                TableProjectionContextData factsData4 = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round4,
                    new StateVersion(4UL),
                    new EventSeq(4UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timing4.CapturedMonotonicMillis,
                    (long)timing4.CapturedAtUtcMs,
                    new[] { event1, event2, event3, event4 }));
                byte[] factsPayload4 = TableProjectionFactsCodec.Encode(factsData4);

                var cp4 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round4,
                    new StateVersion(4UL),
                    new EventSeq(4UL),
                    TableControlCheckpointCodec.Decode(checkpoint.ControlPayload),
                    timing4,
                    harness.Options.RulesetVersion,
                    checkpoint.BootId,
                    timing4.CapturedAtUtcMs,
                    TableCheatLedgerCheckpoint.FromCheatState(cheat3),
                    factsPayload4,
                    5L,
                    timing4.CapturedMonotonicMillis,
                    new[] { event1, event2, event3, event4 },
                    new[] { binding1, binding2, binding3, binding4 });

                beamReq1 = BuildPlayerCommitRequest(
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
                    nextCheatVersion: cheat3.Version,
                    nextCheatFingerprint: cheat3.Fingerprint,
                    nextStatePayload: beamStatePayload,
                    nextStateFingerprint: beamFingerprint,
                    commitCheckpoint: cp4);

                TableDurableCommitResult beamRes1 = await store.CommitAsync(beamReq1, ct);
                Assert.True(beamRes1.IsCommitted);
                TableStateRow stateAfterBeam = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(4UL, stateAfterBeam.StateVersion.Value);
                Assert.Equal(cheat3.Version, stateAfterBeam.CheatVersion);
                Assert.Equal(cheat3.Fingerprint, stateAfterBeam.CheatFingerprint);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                TableDurableCommitResult beamReplay = await store.CommitAsync(beamReq1, ct);
                Assert.True(beamReplay.IsReplayed);
                TableStateRow stateAfterBeamReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(4UL, stateAfterBeamReplay.StateVersion.Value);
                Assert.Equal(cheat3.Version, stateAfterBeamReplay.CheatVersion);
                Assert.Equal(cheat3.Fingerprint, stateAfterBeamReplay.CheatFingerprint);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

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
                    nextCheatVersion: cheat3.Version,
                    nextCheatFingerprint: cheat3.Fingerprint,
                    nextStatePayload: beamStatePayload,
                    nextStateFingerprint: beamFingerprint,
                    commitCheckpoint: cp4);
                TableDurableCommitResult beamConflictRes = await store.CommitAsync(beamConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, beamConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, beamConflictRes.ReasonCode);
                Assert.Equal(4, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                // 2C.d. Immediate Authoritative Cold Recovery 2C (BeamSwap)
                await AssertIntermediateRecoveryAsync(
                    harness,
                    "step2c-beam",
                    expectedStateVersion: 4,
                    expectedRound: round4,
                    expectedTiles: tilesAfterBeam,
                    expectedSeat0Balance: 90,
                    expectedLedgerEntryCount: 3,
                    assertCheat: ac =>
                    {
                        Assert.NotNull(ac.BeamSwap.Offer);
                        Assert.Equal("beam-offer-1", ac.BeamSwap.Offer!.OfferId);
                        Assert.Equal(targetWallIndex, ac.BeamSwap.Offer.Candidates[0].Slot.Index);
                        Assert.Equal(handTileToSwap, ac.BeamSwap.Offer.Candidates[0].Tile);
                    },
                    commitReq: beamReq1,
                    conflictReq: beamConflictReq,
                    expectedOutbox: null,
                    expectedOutboxPayload: null,
                    ct);

                // --- 3. Settlement Action: Tsumo with Outbox and terminal state ---
                var settleReqId = new RequestId("settle-tsumo-1");
                BusinessPayload pSettle = CommandPayloadFactory.SubmitAction(initialSpec.RoundId, "dec-tsumo-1", 0UL, ProtocolActionType.DeclareTsumo, tileIds: new uint[] { (uint)tileToDiscard.Value });
                byte[] settlePayloadA = pSettle.CopyBytes();
                byte[] settlePayloadB = (byte[])settlePayloadA.Clone();
                settlePayloadB[^1] ^= 0xFF;

                var settlementOutbox = new List<TableSettlementOutboxRecord>
                {
                    new TableSettlementOutboxRecord(
                        "match-settle-1",
                        1,
                        outboxPayload,
                        TablePayloadSha256.Compute(outboxPayload),
                        nowUtc)
                };

                var roundEvent5 = new RoundEvent(RoundEventType.RoundEnded, 4, -1, null, "FINISH_ROUND");
                RoundStateMachine round5 = CreateRoundWithEvents(
                    tilesAfterBeam!,
                    initialSpec!,
                    new[]
                    {
                        roundEvent1,
                        roundEvent2,
                        roundEvent3,
                        roundEvent4,
                        roundEvent5
                    },
                    version: 5L,
                    phase: RoundPhase.Discard);
                byte[] settleStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round5));
                ulong settleFingerprint = round5.SourceFingerprint;

                var event5 = new TableEventRecord(
                    new EventSeq(5UL), new StateVersion(5UL), 0,
                    RoundEventType.RoundEnded, -1, null, "FINISH_ROUND", 1,
                    TableEventFactCodec.Encode(new StateVersion(5UL), 0, settleFingerprint));
                var binding5 = new TableCommandBinding(5UL, 1, settleFingerprint);

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing5 = AdvanceTiming(timing4, harness.Clock);
                TableProjectionContextData factsData5 = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round5,
                    new StateVersion(5UL),
                    new EventSeq(5UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timing5.CapturedMonotonicMillis,
                    (long)timing5.CapturedAtUtcMs,
                    new[] { event1, event2, event3, event4, event5 }));
                byte[] factsPayload5 = TableProjectionFactsCodec.Encode(factsData5);

                var cp5 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round5,
                    new StateVersion(5UL),
                    new EventSeq(5UL),
                    TableControlCheckpointCodec.Decode(checkpoint.ControlPayload),
                    timing5,
                    harness.Options.RulesetVersion,
                    checkpoint.BootId,
                    timing5.CapturedAtUtcMs,
                    TableCheatLedgerCheckpoint.FromCheatState(cheat3),
                    factsPayload5,
                    6L,
                    timing5.CapturedMonotonicMillis,
                    new[] { event1, event2, event3, event4, event5 },
                    new[] { binding1, binding2, binding3, binding4, binding5 });

                settleReq1 = BuildPlayerCommitRequest(
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
                    "FINISH_ROUND",
                    settlePayloadA,
                    eventType: RoundEventType.RoundEnded,
                    nextCheatVersion: cheat3.Version,
                    nextCheatFingerprint: cheat3.Fingerprint,
                    terminal: true,
                    outbox: settlementOutbox,
                    nextStatePayload: settleStatePayload,
                    nextStateFingerprint: settleFingerprint,
                    commitCheckpoint: cp5,
                    journalPayload: null);

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

                // 3b. Replay succeeds without duplicate side effects
                TableDurableCommitResult settleReplay = await store.CommitAsync(settleReq1, ct);
                Assert.True(settleReplay.IsReplayed);
                TableStateRow stateAfterSettleReplay = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, stateAfterSettleReplay.StateVersion.Value);
                Assert.Single(await store.ListUnpublishedAsync(ct));
                Assert.Equal(5, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

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
                    "FINISH_ROUND",
                    settlePayloadB,
                    eventType: RoundEventType.RoundEnded,
                    nextCheatVersion: cheat3.Version,
                    nextCheatFingerprint: cheat3.Fingerprint,
                    terminal: true,
                    outbox: settlementOutbox,
                    nextStatePayload: settleStatePayload,
                    nextStateFingerprint: settleFingerprint,
                    commitCheckpoint: cp5,
                    journalPayload: null);
                TableDurableCommitResult settleConflictRes = await store.CommitAsync(settleConflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, settleConflictRes.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, settleConflictRes.ReasonCode);
                Assert.Single(await store.ListUnpublishedAsync(ct));
                Assert.Equal(5, (await store.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);

                // 3d. Immediate Authoritative Cold Recovery 3 (Settlement with Outbox)
                await AssertIntermediateRecoveryAsync(
                    harness,
                    "step3-settle",
                    expectedStateVersion: 5,
                    expectedRound: round5,
                    expectedTiles: tilesAfterBeam,
                    expectedSeat0Balance: 90,
                    expectedLedgerEntryCount: 3,
                    assertCheat: ac =>
                    {
                        Assert.NotNull(ac.BeamSwap.Offer);
                        Assert.Equal("beam-offer-1", ac.BeamSwap.Offer!.OfferId);
                    },
                    commitReq: settleReq1,
                    conflictReq: settleConflictReq,
                    expectedOutbox: settlementOutbox[0],
                    expectedOutboxPayload: outboxPayload,
                    ct);

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

            // --- 4b. Immediate Authoritative Cold Recovery on actual store options (Reviewer Probe Point) ---
            // Verified directly after all 5 commits without any post-hoc SaveCheckpointAsync wiping tail operations!
            harness.Clock.UtcNowMs += 100L;
            harness.Clock.MonotonicMillis += 100L;
            await using (LocalTableRuntime atomicProbe = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                RoundStateMachine probeRound = atomicProbe.ActorForTest.GetRoundStateForHost();
                ConservationReport probeReport = TilePositionMap.Validate(probeRound.Tiles);
                Assert.True(probeReport.IsValid);
                Assert.Equal(136, probeReport.AssignedCount);
                for (int i = 0; i < 136; i++)
                {
                    Assert.Equal(tilesAfterBeam![i].Zone, probeRound.Tiles[i].Zone);
                    Assert.Equal(tilesAfterBeam[i].Index, probeRound.Tiles[i].Index);
                }

                var probeCheatField = typeof(TableActor).GetField("_cheat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var probeCheat = probeCheatField?.GetValue(atomicProbe.ActorForTest) as CheatState;
                Assert.NotNull(probeCheat);
                Assert.Equal(90, probeCheat!.Ledger.BalanceOf(0));
                Assert.Equal(3, probeCheat.Ledger.Entries.Count);
                Assert.Single(probeCheat.TileMarks.Groups);
                Assert.Single(probeCheat.SleeveBloom.Reservations);
                Assert.NotNull(probeCheat.BeamSwap.Offer);
                Assert.Equal(targetWallIndex, probeCheat.BeamSwap.Offer.Candidates[0].Slot.Index);
                Assert.Equal(handTileToSwap, probeCheat.BeamSwap.Offer.Candidates[0].Tile);

                var probeStoreField = typeof(LocalTableRuntime).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var probeStore = probeStoreField?.GetValue(atomicProbe) as LocalTableStore;
                Assert.NotNull(probeStore);

                TableSettlementOutboxRecord? probeOutbox = await probeStore!.TryGetAsync("match-settle-1", 1, ct);
                Assert.NotNull(probeOutbox);
                Assert.Equal(TablePayloadSha256.Compute(outboxPayload), probeOutbox!.PayloadSha256);

                TableDurableReceipt? rDiscardProbe = await probeStore.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct);
                Assert.NotNull(rDiscardProbe);
                TableDurableReceipt? rSettleProbe = await probeStore.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct);
                Assert.NotNull(rSettleProbe);
            }

            // --- 5. Cold Reopen Matrix (Blocker 1 & 2 Regressions) ---
            // 5a. First cold reopen: hydrates all 5 operations under same scope without "Scope id already exists"
            using (LocalTableStore cold1 = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                TableStateRow cold1State = (await cold1.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, cold1State.StateVersion.Value);
                Assert.Equal(7L, cold1State.TimingVersion);
                Assert.Equal(cheat3!.Version, cold1State.CheatVersion);
                Assert.Equal(cheat3.Fingerprint, cold1State.CheatFingerprint);
                Assert.True(cold1State.CheatBound);

                // All 5 receipts exist and match original fields exactly
                TableDurableReceipt rDiscard = (await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct))!;
                AssertReceiptMatches(discardReq1!.Receipt!, rDiscard);

                TableDurableReceipt rMark = (await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-mark-1"), ct))!;
                AssertReceiptMatches(markReq1!.Receipt!, rMark);

                TableDurableReceipt rSleeve = (await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-sleeve-1"), ct))!;
                AssertReceiptMatches(sleeveReq1!.Receipt!, rSleeve);

                TableDurableReceipt rBeam = (await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("cheat-beam-1"), ct))!;
                AssertReceiptMatches(beamReq1!.Receipt!, rBeam);

                TableDurableReceipt rSettle = (await cold1.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct))!;
                AssertReceiptMatches(settleReq1!.Receipt!, rSettle);

                // Exact event records verification from base checkpoint history
                IReadOnlyList<TableEventRecord> events = await cold1.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct);
                Assert.Equal(5, events.Count);
                Assert.Equal(RoundEventType.TileDiscarded, events[0].Type);
                Assert.Equal(RoundEventType.CheatApplied, events[1].Type);
                Assert.Equal(RoundEventType.CheatApplied, events[2].Type);
                Assert.Equal(RoundEventType.CheatApplied, events[3].Type);
                Assert.Equal(RoundEventType.RoundEnded, events[4].Type);

                // Checkpoint committed bindings verified
                TableCheckpoint cpLoaded = (await cold1.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(5UL, cpLoaded.StateVersion.Value);
                IReadOnlyList<TableCommandBinding> bindings = TableCommandBindingsCodec.Decode(cpLoaded.CommittedCommandBindingsPayload!);
                Assert.Equal(5, bindings.Count);

                // Outbox persisted
                TableSettlementOutboxRecord? outboxRec = await cold1.TryGetAsync("match-settle-1", 1, ct);
                Assert.NotNull(outboxRec);
                Assert.Equal(TablePayloadSha256.Compute(outboxPayload), outboxRec!.PayloadSha256);
                Assert.Single(await cold1.ListUnpublishedAsync(ct));

                // 136 Physical entities conservation holds across discard and beam swap
                ConservationReport report = TilePositionMap.Validate(tilesAfterBeam!);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);

                // Replay re-commit returns IsReplayed without advancing state
                var replayDiscard = await cold1.CommitAsync(
                    BuildPlayerCommitRequest(
                        P41Harness.RoomId, "owner-local", cold1.LeaseEpoch, 0UL, 2L,
                        AdvanceTiming(TableTimingCheckpointCodec.Decode(cpLoaded.TimingPayload)),
                        cpLoaded,
                        "player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"),
                        "DISCARD_TILE", discardPayloadA,
                        nextStatePayload: discardStatePayload,
                        nextStateFingerprint: discardFingerprint,
                        tile: tileToDiscard),
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
                Assert.Equal(cheat3!.Version, cold2State.CheatVersion);
                Assert.Equal(cheat3.Fingerprint, cold2State.CheatFingerprint);

                TableDurableReceipt rDiscard2 = (await cold2.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct))!;
                AssertReceiptMatches(discardReq1!.Receipt!, rDiscard2);
                TableDurableReceipt rSettle2 = (await cold2.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct))!;
                AssertReceiptMatches(settleReq1!.Receipt!, rSettle2);

                Assert.Equal(5, (await cold2.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Single(await cold2.ListUnpublishedAsync(ct));
            }

            // --- 6. Authoritative Cold Recovery Matrix (Blocker D-R1-02 Domain Probe) ---
            for (int run = 1; run <= 2; run++)
            {
                await using (LocalTableRuntime recovered = await LocalTableRuntime.OpenAsync(harness.Options, ct))
                {
                    RoundStateMachine recoveredRound = recovered.ActorForTest.GetRoundStateForHost();
                    TilePositionMap recoveredTiles = recoveredRound.Tiles;

                    // 6a. 136 Physical entities conservation & exact position match on RECOVERED state
                    ConservationReport report = TilePositionMap.Validate(recoveredTiles);
                    Assert.True(report.IsValid, string.Join("; ", report.Errors));
                    Assert.Equal(136, report.AssignedCount);
                    Assert.Equal(0, report.UnassignedCount);

                    for (int i = 0; i < 136; i++)
                    {
                        TilePosition expected = tilesAfterBeam![i];
                        TilePosition actual = recoveredTiles[i];
                        Assert.True(
                            expected.Zone == actual.Zone && expected.Index == actual.Index,
                            $"Run {run} tile {i} differs: expected {expected.Zone}:{expected.Index}, actual {actual.Zone}:{actual.Index}");
                    }

                    // 6b. Field-by-field authoritative cheat state verification
                    var cheatField = typeof(TableActor).GetField("_cheat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var actorCheat = cheatField?.GetValue(recovered.ActorForTest) as CheatState;
                    Assert.NotNull(actorCheat);
                    Assert.Equal(90, actorCheat.Ledger.BalanceOf(0));
                    Assert.Equal(3, actorCheat.Ledger.Entries.Count);
                    Assert.NotEmpty(actorCheat.TileMarks.Groups);
                    Assert.NotEmpty(actorCheat.SleeveBloom.Reservations);
                    Assert.NotNull(actorCheat.BeamSwap.Offer);
                    Assert.Equal(targetWallIndex, actorCheat.BeamSwap.Offer.Candidates[0].Slot.Index);

                    // 6c. Canonical byte verification of 3 tricks
                    BusinessPayload pMarks = CommandPayloadFactory.SubmitTileMark(initialSpec!.RoundId, "dec-mark-1", 0UL, new uint[] { 10, 11, 12, 13 });
                    BusinessPayload pSleeve = CommandPayloadFactory.SubmitSleeveBloom(initialSpec.RoundId, "dec-sleeve-1", 0UL);
                    BusinessPayload pBeam = CommandPayloadFactory.SubmitBeamSwap(initialSpec.RoundId, "dec-beam-1", 0UL);
                    Assert.NotEmpty(pMarks.CopyBytes());
                    Assert.NotEmpty(pSleeve.CopyBytes());
                    Assert.NotEmpty(pBeam.CopyBytes());

                    // 6d. Terminal & Outbox record verification
                    var storeField = typeof(LocalTableRuntime).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var runtimeStore = storeField?.GetValue(recovered) as LocalTableStore;
                    Assert.NotNull(runtimeStore);

                    TableSettlementOutboxRecord? outboxRec = await runtimeStore.TryGetAsync("match-settle-1", 1, ct);
                    Assert.NotNull(outboxRec);
                    Assert.Equal(TablePayloadSha256.Compute(outboxPayload), outboxRec!.PayloadSha256);
                    Assert.Single(await runtimeStore.ListUnpublishedAsync(ct));

                    // 6e. Receipts queryable
                    TableDurableReceipt? rDiscardRec = await runtimeStore.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("action-discard-1"), ct);
                    Assert.NotNull(rDiscardRec);
                    Assert.Equal(ReceiptStatus.Applied, rDiscardRec!.Receipt.Status);

                    TableDurableReceipt? rSettleRec = await runtimeStore.TryGetAsync("player-account-0", new RequestScopeId("scope-valid-1"), new RequestId("settle-tsumo-1"), ct);
                    Assert.NotNull(rSettleRec);
                    Assert.Equal(ReceiptStatus.Applied, rSettleRec!.Receipt.Status);

                    // 6f. Replay already committed request on recovered runtime produces no secondary side effects
                    TableDurableCommitResult replayOnRecovered = await runtimeStore.CommitAsync(discardReq1!, ct);
                    Assert.True(replayOnRecovered.IsReplayed);

                    // 6g. Replay with conflicting bytes rejected
                    TableDurableCommitResult conflictOnRecovered = await runtimeStore.CommitAsync(discardConflictReq!, ct);
                    Assert.Equal(TableCommitDisposition.Conflict, conflictOnRecovered.Disposition);
                    Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflictOnRecovered.ReasonCode);
                }
            }

            // --- 7. Advance clock past scope expiry: historical receipts remain queryable, but NEW requests are rejected ---
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
                        "DISCARD_TILE", discardPayloadA,
                        nextStatePayload: discardStatePayload,
                        nextStateFingerprint: discardFingerprint,
                        tile: tileToDiscard),
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
                    8L,
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

        [Fact]
        public async Task ReviewerProbe_ScopeRetentionDoesNotExtendAuthorization()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            string accountId = "probe-account";
            string scopeIdStr = "probe-short-scope";
            var scopeId = new RequestScopeId(scopeIdStr);
            var req1Id = new RequestId("req-1");
            var req2Id = new RequestId("req-2");

            ulong nowUtc;
            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                nowUtc = (ulong)harness.Clock.UtcNowMs;

                // Scope validity: 60 seconds (now - 1000 to now + 60_000)
                store.IssueScope(
                    accountId,
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    issuedAtUtcMs: nowUtc - 1_000UL,
                    expiresAtUtcMs: nowUtc + 60_000UL,
                    scopeId: scopeIdStr);

                TableCheckpoint cp0 = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing0 = TableTimingCheckpointCodec.Decode(cp0.TimingPayload);
                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing1 = AdvanceTiming(timing0, harness.Clock);

                // Commit 1: receipt retention is 10 minutes (now to now + 600_000)
                var commitReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    expectedStateVersion: 0UL,
                    timingVersion: 2L,
                    timing: timing1,
                    checkpoint: cp0,
                    accountId: accountId,
                    scopeId: scopeId,
                    requestId: req1Id,
                    api: "DISCARD_TILE",
                    businessPayload: new byte[] { 1, 2, 3 },
                    createdAtUtcMs: nowUtc,
                    expiresAtUtcMs: nowUtc + 600_000UL);

                TableDurableCommitResult res1 = await store.CommitAsync(commitReq1, ct);
                Assert.True(res1.IsCommitted, "res1=" + res1.Disposition + " " + res1.ReasonCode);
            }

            // Advance clock by 120 seconds (now + 120_000): well past 60s scope expiration,
            // but still within the 600s receipt retention window.
            harness.Clock.UtcNowMs += 120_000L;

            using (LocalTableStore cold = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, fs, ct))
            {
                // Old receipt is still queryable under historical retention
                TableDurableReceipt? oldReceipt = await cold.TryGetAsync(accountId, scopeId, req1Id, ct);
                Assert.NotNull(oldReceipt);
                Assert.Equal(req1Id, oldReceipt.RequestId);

                // Submitting identical request 1 again succeeds as Replayed (idempotent replay)
                TableCheckpoint cpCold = (await cold.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timingCold = AdvanceTiming(TableTimingCheckpointCodec.Decode(cpCold.TimingPayload));
                var replayReq1 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    cold.LeaseEpoch,
                    expectedStateVersion: 0UL,
                    timingVersion: 2L,
                    timing: timingCold,
                    checkpoint: cpCold,
                    accountId: accountId,
                    scopeId: scopeId,
                    requestId: req1Id,
                    api: "DISCARD_TILE",
                    businessPayload: new byte[] { 1, 2, 3 },
                    createdAtUtcMs: nowUtc,
                    expiresAtUtcMs: nowUtc + 600_000UL);
                TableDurableCommitResult replayRes1 = await cold.CommitAsync(replayReq1, ct);
                Assert.True(replayRes1.IsReplayed);

                // But submitting a brand NEW request with this scope is strictly rejected!
                // Receipt retention does NOT extend authorization for new requests.
                ulong nowCold = (ulong)harness.Clock.UtcNowMs;
                TableTimingCheckpoint timingColdNext = AdvanceTiming(AdvanceTiming(timingCold));
                var newReq2 = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    cold.LeaseEpoch,
                    expectedStateVersion: 1UL,
                    timingVersion: 3L,
                    timing: timingColdNext,
                    checkpoint: cpCold,
                    accountId: accountId,
                    scopeId: scopeId,
                    requestId: req2Id,
                    api: "DISCARD_TILE",
                    businessPayload: new byte[] { 4, 5, 6 },
                    createdAtUtcMs: nowCold,
                    expiresAtUtcMs: nowCold + 600_000UL);

                TableDurableCommitResult res2 = await cold.CommitAsync(newReq2, ct);
                Assert.Equal(TableCommitDisposition.ScopeRejected, res2.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestScopeExpired, res2.ReasonCode);
            }
        }

        private static void AssertReceiptMatches(TableDurableReceipt expected, TableDurableReceipt? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.AccountId, actual!.AccountId);
            Assert.Equal(expected.ScopeId, actual.ScopeId);
            Assert.Equal(expected.RequestId, actual.RequestId);
            Assert.Equal(expected.Api, actual.Api);
            Assert.Equal(expected.PayloadSha256, actual.PayloadSha256);
            Assert.Equal(expected.Receipt.EvaluatedStateVersion, actual.Receipt.EvaluatedStateVersion);
            Assert.Equal(expected.Receipt.CommandVersion, actual.Receipt.CommandVersion);
            Assert.Equal(expected.Receipt.EventSeq, actual.Receipt.EventSeq);
            Assert.Equal(expected.Receipt.Status, actual.Receipt.Status);
            Assert.Equal(expected.CreatedAtUtcMs, actual.CreatedAtUtcMs);
            Assert.Equal(expected.ExpiresAtUtcMs, actual.ExpiresAtUtcMs);
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
                    TableEventFactCodec.Encode(
                        new StateVersion(expectedStateVersion + 1UL),
                        0,
                        checkpoint.StateFingerprint + 1UL))
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
            ulong? nextStateFingerprint = null,
            TileId? tile = null,
            TableCheckpoint? commitCheckpoint = null,
            byte[]? journalPayload = null)
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
            ulong effectiveFingerprint = nextStateFingerprint ?? (checkpoint.StateFingerprint + expectedStateVersion + 1UL);
            int effectiveSeat = eventType == RoundEventType.RoundEnded ? -1 : 0;
            TileId? effectiveTile = eventType == RoundEventType.RoundEnded
                ? (TileId?)null
                : (tile ?? new TileId((int)(expectedStateVersion % 136)));
            var events = new List<TableEventRecord>
            {
                new TableEventRecord(
                    new EventSeq(expectedStateVersion + 1UL),
                    new StateVersion(expectedStateVersion + 1UL),
                    0,
                    eventType,
                    effectiveSeat,
                    effectiveTile,
                    api,
                    1,
                    TableEventFactCodec.Encode(
                        new StateVersion(expectedStateVersion + 1UL),
                        0,
                        effectiveFingerprint))
            };
            byte[] effectiveJournalPayload = journalPayload ?? businessPayload;
            var journal = new TableCommandJournalRecord(
                new StateVersion(expectedStateVersion + 1UL),
                new StateVersion(expectedStateVersion),
                requestId,
                accountId,
                scopeId,
                api,
                receiptDigest,
                effectiveJournalPayload);

            byte[] effectiveStatePayload = nextStatePayload ?? checkpoint.StatePayload;

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
                checkpoint: commitCheckpoint,
                nextStatePayload: effectiveStatePayload,
                nextStateFingerprint: effectiveFingerprint,
                terminal: terminal,
                timing: timing,
                timingVersion: timingVersion,
                committedFactsPayload: commitCheckpoint?.CommittedFactsPayload ?? checkpoint.CommittedFactsPayload,
                committedFactsPayloadSha256: commitCheckpoint?.CommittedFactsPayloadSha256 ?? checkpoint.CommittedFactsPayloadSha256,
                nextCheatVersion: nextCheatVersion,
                nextCheatFingerprint: nextCheatFingerprint);
        }

        private static async Task AssertIntermediateRecoveryAsync(
            P41Harness harness,
            string stepTag,
            int expectedStateVersion,
            RoundStateMachine expectedRound,
            TilePositionMap expectedTiles,
            int expectedSeat0Balance,
            int expectedLedgerEntryCount,
            Action<CheatState>? assertCheat,
            TableDurableCommitRequest commitReq,
            TableDurableCommitRequest conflictReq,
            TableSettlementOutboxRecord? expectedOutbox,
            byte[]? expectedOutboxPayload,
            CancellationToken ct)
        {
            string probeDir = Path.Combine(harness.Root, "probe-" + stepTag);
            Directory.CreateDirectory(probeDir);
            string probeSavePath = Path.Combine(probeDir, P41Harness.RoomId + ".save");
            File.Copy(harness.SavePath, probeSavePath, overwrite: true);

            var probeOptions = P41Harness.CloneOptions(harness.Options);
            probeOptions.SaveDirectory = probeDir;
            probeOptions.FileSystemForTest = new SystemLocalFileSystem();
            var probeClock = new ManualClock
            {
                UtcNowMs = harness.Clock.UtcNowMs + 50L,
                MonotonicMillis = harness.Clock.MonotonicMillis + 50L
            };
            probeOptions.Environment = new LocalHostEnvironment
            {
                Clock = probeClock,
                NewId = harness.Options.Environment?.NewId
            };

            await using (LocalTableRuntime recovered = await LocalTableRuntime.OpenAsync(probeOptions, ct))
            {
                RoundStateMachine recoveredRound = recovered.ActorForTest.GetRoundStateForHost();
                TilePositionMap recoveredTiles = recoveredRound.Tiles;

                // 1. 136 Physical entities conservation & exact position match
                ConservationReport report = TilePositionMap.Validate(recoveredTiles);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);

                for (int i = 0; i < 136; i++)
                {
                    TilePosition expected = expectedTiles[i];
                    TilePosition actual = recoveredTiles[i];
                    Assert.True(
                        expected.Zone == actual.Zone && expected.Index == actual.Index,
                        $"Step {stepTag} tile {i} differs: expected {expected.Zone}:{expected.Index}, actual {actual.Zone}:{actual.Index}");
                }

                // 2. Balances & ledger entries
                var cheatField = typeof(TableActor).GetField("_cheat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var actorCheat = cheatField?.GetValue(recovered.ActorForTest) as CheatState;
                Assert.NotNull(actorCheat);
                Assert.Equal(expectedSeat0Balance, actorCheat!.Ledger.BalanceOf(0));
                Assert.Equal(expectedLedgerEntryCount, actorCheat.Ledger.Entries.Count);
                assertCheat?.Invoke(actorCheat);

                // 3. Receipts, events, and outbox in restored store
                var storeField = typeof(LocalTableRuntime).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var runtimeStore = storeField?.GetValue(recovered) as LocalTableStore;
                Assert.NotNull(runtimeStore);

                TableDurableReceipt? receiptRec = await runtimeStore!.TryGetAsync(
                    commitReq.Receipt!.AccountId,
                    commitReq.Receipt.ScopeId,
                    commitReq.Receipt.RequestId,
                    ct);
                AssertReceiptMatches(commitReq.Receipt, receiptRec);

                IReadOnlyList<TableEventRecord> events = await runtimeStore.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct);
                Assert.Equal(expectedStateVersion, events.Count);

                if (expectedOutbox != null)
                {
                    TableSettlementOutboxRecord? outboxRec = await runtimeStore.TryGetAsync(expectedOutbox.MatchId, expectedOutbox.ResultVersion, ct);
                    Assert.NotNull(outboxRec);
                    Assert.Equal(TablePayloadSha256.Compute(expectedOutboxPayload!), outboxRec!.PayloadSha256);
                    Assert.Single(await runtimeStore.ListUnpublishedAsync(ct));
                }

                // 4. Replay committed request returns IsReplayed
                TableDurableCommitResult replayOnRecovered = await runtimeStore.CommitAsync(commitReq, ct);
                Assert.True(replayOnRecovered.IsReplayed);

                // 5. Conflict request returns Conflict
                TableDurableCommitResult conflictOnRecovered = await runtimeStore.CommitAsync(conflictReq, ct);
                Assert.Equal(TableCommitDisposition.Conflict, conflictOnRecovered.Disposition);
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflictOnRecovered.ReasonCode);

                // 6. Zero secondary side effects after replay and conflict
                TableStateRow stateAfterReplay = (await runtimeStore.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal((ulong)expectedStateVersion, stateAfterReplay.StateVersion.Value);
                Assert.Equal(expectedStateVersion, (await runtimeStore.LoadEventsAfterAsync(P41Harness.RoomId, new EventSeq(0), ct)).Count);
                Assert.Equal(expectedSeat0Balance, actorCheat.Ledger.BalanceOf(0));
            }
        }

        [Fact]
        public async Task SaveCheckpoint_CompactsTailOperationsAndRecoversCleanly()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem();
            harness.Options.FileSystemForTest = fs;
            CancellationToken ct = TestContext.Current.CancellationToken;

            using (LocalTableStore store = await OpenFreshStoreAsync(harness, fs, ct))
            {
                await store.AcquireAsync(P41Harness.RoomId, "owner-local", TimeSpan.FromMinutes(5), ct);
                TableCheckpoint checkpoint0 = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                TableTimingCheckpoint timing0 = TableTimingCheckpointCodec.Decode(checkpoint0.TimingPayload);
                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timing1 = AdvanceTiming(timing0, harness.Clock);

                string accountId = "player-account-0";
                var scope = store.IssueScope(
                    accountId,
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    issuedAtUtcMs: (ulong)harness.Clock.UtcNowMs - 1000UL,
                    expiresAtUtcMs: (ulong)harness.Clock.UtcNowMs + 600_000UL,
                    scopeId: "scope-compact-1");

                RoundRecoveryState initialRound = RoundRecoveryCodec.Decode(checkpoint0.StatePayload);
                IReadOnlyList<TileId> handTiles = initialRound.Tiles.TilesInZone(TileZone.Hand, 0);
                TileId tileToDiscard = handTiles[0];
                TransitionResult discardTransition = initialRound.Tiles.Discard(0, tileToDiscard);
                Assert.True(discardTransition.IsSuccess);
                TilePositionMap tilesAfterDiscard = discardTransition.RequireState();

                var roundEvent1 = new RoundEvent(RoundEventType.TileDiscarded, 0, 0, tileToDiscard, "DISCARD_TILE");
                RoundStateMachine round1 = CreateRoundWithEvents(tilesAfterDiscard, initialRound.Spec, new[] { roundEvent1 }, version: 1L, phase: RoundPhase.Discard);
                byte[] discardStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round1));
                ulong discardFingerprint = round1.SourceFingerprint;

                byte[] journalPayload = RoundCommandCodec.Encode(RoundCommand.Discard(initialRound.Spec.RoundId, 0, tileToDiscard));

                // Commit with commitCheckpoint: null so that operation accumulates in tail operations
                var commitReq = BuildPlayerCommitRequest(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    0UL,
                    2L,
                    timing1,
                    checkpoint0,
                    accountId,
                    scope.ScopeId,
                    new RequestId("req-discard-tail"),
                    "DISCARD_TILE",
                    businessPayload: journalPayload,
                    nextStatePayload: discardStatePayload,
                    nextStateFingerprint: discardFingerprint,
                    tile: tileToDiscard,
                    commitCheckpoint: null,
                    journalPayload: journalPayload);

                TableDurableCommitResult res = await store.CommitAsync(commitReq, ct);
                Assert.True(res.IsCommitted);

                // Before SaveCheckpoint: StateVersion is 1, base checkpoint is at version 0
                TableStateRow stateBeforeCp = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, stateBeforeCp.StateVersion.Value);

                // Now SaveCheckpoint at StateVersion 1
                var facts = new LocalSnapshotFacts(harness.Options.Environment!, harness.Clock);
                var event1 = commitReq.Events[0];
                var binding1 = new TableCommandBinding(1UL, 1, discardFingerprint);

                harness.Clock.UtcNowMs += 1000L;
                harness.Clock.MonotonicMillis += 1000L;
                TableTimingCheckpoint timingCp = AdvanceTiming(timing1, harness.Clock);
                TableProjectionContextData factsData = facts.Capture(new TableProjectionContextRequest(
                    P41Harness.RoomId,
                    round1,
                    new StateVersion(1UL),
                    new EventSeq(1UL),
                    store.LeaseEpoch,
                    0L,
                    0L,
                    timingCp.CapturedMonotonicMillis,
                    (long)timingCp.CapturedAtUtcMs,
                    new[] { event1 }));
                byte[] factsPayload = TableProjectionFactsCodec.Encode(factsData);

                var cp1 = TableCheckpointFactory.Create(
                    P41Harness.RoomId,
                    round1,
                    new StateVersion(1UL),
                    new EventSeq(1UL),
                    TableControlCheckpointCodec.Decode(checkpoint0.ControlPayload),
                    timingCp,
                    harness.Options.RulesetVersion,
                    checkpoint0.BootId,
                    timingCp.CapturedAtUtcMs,
                    TableCheatLedgerCheckpointCodec.Decode(checkpoint0.CheatPayload!),
                    factsPayload,
                    3L,
                    timingCp.CapturedMonotonicMillis,
                    new[] { event1 },
                    new[] { binding1 });

                var fence = new TableCheckpointFence(
                    P41Harness.RoomId,
                    "owner-local",
                    store.LeaseEpoch,
                    new StateVersion(1UL),
                    0L,
                    2L);

                TableDurableCommitResult cpRes = await store.SaveCheckpointAsync(cp1, fence, ct);
                Assert.True(cpRes.IsCommitted, $"cpRes failed: {cpRes.Disposition} {cpRes.ReasonCode} {cpRes.Detail}");
            }

            // Now verify cold recovery after SaveCheckpoint tail compaction
            harness.Clock.UtcNowMs += 100L;
            harness.Clock.MonotonicMillis += 100L;
            await using (LocalTableRuntime recovered = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                RoundStateMachine recoveredRound = recovered.ActorForTest.GetRoundStateForHost();
                Assert.Equal(1L, recoveredRound.Version);
                ConservationReport report = TilePositionMap.Validate(recoveredRound.Tiles);
                Assert.True(report.IsValid);
                Assert.Equal(136, report.AssignedCount);

                var storeField = typeof(LocalTableRuntime).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var runtimeStore = storeField?.GetValue(recovered) as LocalTableStore;
                Assert.NotNull(runtimeStore);

                TableCheckpoint loadedCp = (await runtimeStore!.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(1UL, loadedCp.StateVersion.Value);

                TableDurableReceipt? receipt = await runtimeStore.TryGetAsync("player-account-0", new RequestScopeId("scope-compact-1"), new RequestId("req-discard-tail"), ct);
                Assert.NotNull(receipt);
            }
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

        private static RoundStateMachine CreateRoundWithEvents(
            TilePositionMap tiles,
            RoundSpec spec,
            IReadOnlyList<RoundEvent> events,
            long version = 5L,
            RoundPhase phase = RoundPhase.RoundEnd,
            int currentSeat = 0,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null,
            IReadOnlyList<RoundMeld>? melds = null,
            int pendingKanDoraCount = 0)
        {
            var dummyState = new RoundRecoveryState(
                spec, version, phase, currentSeat, tiles, null, null, events,
                sourceFingerprint: 0UL,
                scores, riichiSticks, riichiDeclared, melds, pendingKanDoraCount);
            ulong realFingerprint = 0UL;
            try
            {
                RoundRecovery.Restore(dummyState);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("Recovered round fingerprint "))
            {
                string[] parts = ex.Message.Split(' ');
                realFingerprint = ulong.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            }

            var realState = new RoundRecoveryState(
                spec, version, phase, currentSeat, tiles, null, null, events,
                realFingerprint,
                scores, riichiSticks, riichiDeclared, melds, pendingKanDoraCount);
            return RoundRecovery.Restore(realState);
        }
    }
}
