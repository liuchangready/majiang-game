using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
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

        public int CopyAttempts { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public Stream CreateNew(string path) => _inner.CreateNew(path);

        public Stream OpenRead(string path) => _inner.OpenRead(path);

        public Stream OpenExclusive(string path) => _inner.OpenExclusive(path);

        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);

        public void Move(string source, string destination)
        {
            Exception? before = BeforeMove?.Invoke(destination);
            if (before != null)
            {
                throw before;
            }

            _inner.Move(source, destination);
            Exception? after = AfterMove?.Invoke(destination);
            if (after != null)
            {
                throw after;
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            Exception? before = BeforeReplace?.Invoke(destination);
            if (before != null)
            {
                throw before;
            }

            DuringReplace?.Invoke();
            _inner.Replace(source, destination, backup);
            Exception? after = AfterReplace?.Invoke(destination);
            if (after != null)
            {
                throw after;
            }
        }

        public void Copy(string source, string destination, bool overwrite) =>
            _inner.Copy(source, destination, overwrite);

        public void Delete(string path) => _inner.Delete(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
    }

    public sealed class LocalTableStoreTests
    {
        [Fact]
        public async Task MoveSucceededButCopyReportedFailureStillLeavesReadablePrimary()
        {
            await using var harness = new P41Harness();
            var fs = new FaultingFileSystem
            {
                AfterMove = _ => new IOException("injected copy failure")
            };
            harness.Options.FileSystemForTest = fs;
            LocalTableStore first = await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken);
            first.Dispose();
            Assert.True(File.Exists(harness.SavePath));

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
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await OpenFreshStoreAsync(harness, fs, TestContext.Current.CancellationToken));
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
                Assert.Equal(before.Length, new FileInfo(harness.SavePath).Length);

                await Assert.ThrowsAsync<LocalTableException>(
                    async () => await SaveCheckpointAsync(store, checkpoint, expectedTiming: 1L, TestContext.Current.CancellationToken));            }
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
            await store.CreateRoomAsync(checkpoint, cancellationToken);
            return store;
        }

        private static async Task<TableCheckpoint> BuildNextCheckpointAsync(LocalTableStore store)
        {
            TableCheckpoint initial = await store.LoadCheckpointAsync(
                P41Harness.RoomId,
                TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("missing checkpoint");
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
                initial.TimingPayload,
                initial.TimingPayloadSha256,
                initial.CheatPayload,
                initial.CheatPayloadSha256,
                initial.CommittedFactsPayload,
                initial.CommittedFactsPayloadSha256,
                initial.CreatedAtUtcMs,
                initial.BootId,
                initial.SchemaVersion,
                initial.TimingVersion + 1L,
                initial.CapturedMonotonicMillis,
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
    }
}
