using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Client.Tests
{
    /// <summary>
    /// Real child-process tests. These prove crash/restart behavior of the
    /// on-disk snapshot on the host file system; they do not simulate power
    /// loss, a disk cache flush failure, Android/iOS fsync, or a platform
    /// secure-storage boundary.
    /// </summary>
    public sealed class FileOutboxProcessRestartTests
    {
        [Fact]
        public async Task KilledBeforeSaveLeavesNoCommittedRecord()
        {
            using (var directory = new TempDirectory())
            {
                var worker = StartWorker("save-wait", directory.Path, "request-1");
                await using (worker)
                {
                    Assert.Equal("READY", await worker.ReadLineAsync(TimeSpan.FromSeconds(20)));
                    await worker.KillAsync();
                }

                var store = new FileOutboxStore(directory.Path);
                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.False(File.Exists(store.StorePath));
            }
        }

        [Fact]
        public async Task KilledDuringCommitLeavesThePreviousSnapshotValid()
        {
            using (var directory = new TempDirectory())
            {
                var seed = new FileOutboxStore(directory.Path);
                OutboxEntry first = Entry("request-1");
                await seed.SaveAsync(first, CancellationToken.None);
                byte[] committedBefore = File.ReadAllBytes(seed.StorePath);

                var worker = StartWorker("save-block", directory.Path, "request-2", "AfterTempFlush");
                await using (worker)
                {
                    Assert.Equal("READY", await worker.ReadLineAsync(TimeSpan.FromSeconds(20)));
                    string phase = await worker.ReadLineAsync(TimeSpan.FromSeconds(20));
                    Assert.StartsWith("PHASE AfterTempFlush", phase, StringComparison.Ordinal);

                    // The temp file is a complete prospective snapshot, but the
                    // committed file has not been replaced yet.
                    Assert.NotEmpty(Directory.GetFiles(directory.Path, "*.tmp-*"));
                    Assert.Equal(committedBefore, File.ReadAllBytes(seed.StorePath));

                    await worker.KillAsync();
                }

                var reopened = new FileOutboxStore(directory.Path);
                IReadOnlyList<OutboxEntry> loaded = await reopened.LoadUnresolvedAsync(CancellationToken.None);
                OutboxEntry actual = Assert.Single(loaded);
                Assert.Equal("request-1", actual.RequestId.Value);
                Assert.Equal(first.CopyCanonicalPayload(), actual.CopyCanonicalPayload());
                Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
            }
        }

        [Fact]
        public async Task KilledAfterCommitKeepsExactBytesAndMetadata()
        {
            using (var directory = new TempDirectory())
            {
                var worker = StartWorker("save-hold", directory.Path, "request-1");
                string saved;
                await using (worker)
                {
                    saved = await worker.ReadLineAsync(TimeSpan.FromSeconds(20));
                    Assert.StartsWith("SAVED request-1 ", saved, StringComparison.Ordinal);
                    await worker.KillAsync();
                }

                (int exitCode, string output, string error) = await RunToExitAsync("load", directory.Path);
                Assert.Equal(0, exitCode);
                Assert.Equal(string.Empty, error);
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal("COUNT 1", lines[0]);
                Assert.StartsWith("ENTRY request-1 scope-1 account-1 10 1 5 round-1 decision-1 7 3 session-1 room-1 ", lines[1], StringComparison.Ordinal);

                string[] fields = lines[1].Split(' ');
                Assert.Equal("request-1", fields[1]);
                Assert.Equal("scope-1", fields[2]);
                Assert.Equal("account-1", fields[3]);
                Assert.Equal("10", fields[4]);
                Assert.Equal("1", fields[5]);
                Assert.Equal("5", fields[6]);
                Assert.Equal("round-1", fields[7]);
                Assert.Equal("decision-1", fields[8]);
                Assert.Equal("7", fields[9]);
                Assert.Equal("3", fields[10]);
                Assert.Equal("session-1", fields[11]);
                Assert.Equal("room-1", fields[12]);
                Assert.Equal(ExpectedPayloadDigest(), fields[13]);
                Assert.Equal(ExpectedPayload().Length.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[14]);
                Assert.Equal(ExpectedPayload(), Convert.FromBase64String(fields[15]));
            }
        }

        [Fact]
        public async Task KilledBeforeResolveKeepsTheUnknownEntryForReceiptQuery()
        {
            using (var directory = new TempDirectory())
            {
                (int saveExit, _, string saveError) = await RunToExitAsync("save", directory.Path, "request-1");
                Assert.Equal(0, saveExit);
                Assert.Equal(string.Empty, saveError);

                var waiter = StartWorker("resolve-wait", directory.Path, "request-1");
                await using (waiter)
                {
                    Assert.Equal("READY request-1", await waiter.ReadLineAsync(TimeSpan.FromSeconds(20)));
                    await waiter.KillAsync();
                }

                var reopened = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Assert.Single(await reopened.LoadUnresolvedAsync(CancellationToken.None));
                Assert.Equal("request-1", entry.RequestId.Value);
                Assert.Equal("scope-1", entry.ScopeId.Value);
            }
        }

        [Fact]
        public async Task ConcurrentWorkerProcessesSerializeCommitsAndPreserveEveryEntry()
        {
            using (var directory = new TempDirectory())
            {
                const int workers = 6;
                var processes = new List<WorkerProcess>(workers);
                try
                {
                    for (int i = 0; i < workers; i++)
                    {
                        processes.Add(StartWorker(
                            "save",
                            directory.Path,
                            "request-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }

                    for (int i = 0; i < processes.Count; i++)
                    {
                        await processes[i].WaitForExitAsync(TimeSpan.FromSeconds(60));
                        Assert.Equal(0, processes[i].ExitCode);
                        Assert.Equal(string.Empty, processes[i].ReadAllStandardError());
                    }
                }
                finally
                {
                    for (int i = 0; i < processes.Count; i++)
                    {
                        await processes[i].DisposeAsync();
                    }
                }

                var store = new FileOutboxStore(directory.Path);
                IReadOnlyList<OutboxEntry> loaded = await store.LoadUnresolvedAsync(CancellationToken.None);
                Assert.Equal(workers, loaded.Count);
                Assert.Equal(
                    Enumerable.Range(0, workers)
                        .Select(i => "request-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    loaded.Select(entry => entry.RequestId.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public async Task UnknownOutcomeAfterARealWorkerExitIsQueriedNotResent()
        {
            using (var directory = new TempDirectory())
            {
                (int saveExit, _, string saveError) = await RunToExitAsync("save", directory.Path, "request-1");
                Assert.Equal(0, saveExit);
                Assert.Equal(string.Empty, saveError);

                var store = new FileOutboxStore(directory.Path);
                var channel = new FakeTableChannel();
                var session = new TableSession(channel, store);
                await session.OpenAsync(new TableSessionOpenRequest("ticket-2", "client-build-1"), CancellationToken.None);
                channel.Enqueue(TestWire.Welcome());
                await session.PumpOnceAsync(CancellationToken.None);
                channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
                await session.PumpOnceAsync(CancellationToken.None);

                Assert.Equal(0, channel.CountSent(Mahjong.Contracts.Wire.MessageType.Command));
                Assert.Equal(1, channel.CountSent(Mahjong.Contracts.Wire.MessageType.ReceiptQuery));
                Mahjong.Contracts.Wire.Envelope query = TestWire.OutboundReceiptQuery(channel, "request-1");
                Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
                PendingCommandSnapshot pending = Assert.Single(session.PendingCommands);
                Assert.True(pending.DeliveryUnknown);
                Assert.True(pending.NeedsReconciliation);
            }
        }

        [Fact]
        public async Task KilledAfterResolveDoesNotResurrectTheRequest()
        {
            using (var directory = new TempDirectory())
            {
                var worker = StartWorker("resolve-hold", directory.Path, "request-1");
                await using (worker)
                {
                    string resolved = await worker.ReadLineAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal("RESOLVED request-1", resolved);
                    await worker.KillAsync();
                }

                (int loadExit, string loadOutput, _) = await RunToExitAsync("load", directory.Path);
                Assert.Equal(0, loadExit);
                Assert.Equal("COUNT 0", loadOutput.Trim());

                (int saveExit, string saveOutput, string saveError) = await RunToExitAsync("save-after-resolve", directory.Path, "request-1");
                Assert.Equal(0, saveExit);
                Assert.Equal("RESOLVED", saveOutput.Trim());
                Assert.Equal(string.Empty, saveError);
            }
        }

        private static async Task<(int ExitCode, string Output, string Error)> RunToExitAsync(params string[] args)
        {
            var worker = StartWorker(args);
            await using (worker)
            {
                await worker.WaitForExitAsync(TimeSpan.FromSeconds(30));
                return (worker.ExitCode, worker.ReadAllStandardOutput(), worker.ReadAllStandardError());
            }
        }

        private static WorkerProcess StartWorker(params string[] args)
        {
            string workerPath = Path.Combine(AppContext.BaseDirectory, "outbox-worker", "Mahjong.Client.OutboxWorker.dll");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException(
                    "The test-only outbox worker was not copied next to the test assembly. Build the test project first.",
                    workerPath);
            }

            var startInfo = new ProcessStartInfo(DotnetPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(workerPath);
            for (int i = 0; i < args.Length; i++)
            {
                startInfo.ArgumentList.Add(args[i]);
            }

            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the outbox worker process.");
            return new WorkerProcess(process);
        }

        private static string DotnetPath
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable("MAHJONG_DOTNET");
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                {
                    return configured;
                }

                configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                {
                    return configured;
                }

                string? current = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(current) &&
                    string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                const string SdkPath = @"D:\Unity\dotnet\dotnet.exe";
                if (File.Exists(SdkPath))
                {
                    return SdkPath;
                }

                return "dotnet";
            }
        }

        private static OutboxEntry Entry(string requestId)
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                7,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 5, 6 });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId("scope-1"),
                new StateVersion(5),
                new RoundId("round-1"),
                new DecisionId("decision-1"),
                new ControlEpoch(7),
                new OwnerEpoch(3));
            var scope = new RequestScope(
                new RequestScopeId("scope-1"),
                "account-1",
                ProtocolRole.Player,
                new RoomId("room-1"),
                1000,
                2000);
            return new OutboxEntry(command, scope, new SessionId("session-1"), new RoomId("room-1"));
        }

        private static byte[] ExpectedPayload()
        {
            return CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                7,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 5, 6 }).CopyBytes();
        }

        private static string ExpectedPayloadDigest()
        {
            return CanonicalPayloadDigest.Compute(ExpectedPayload()).ToHex();
        }

        private sealed class WorkerProcess : IAsyncDisposable
        {
            private readonly Process _process;

            public WorkerProcess(Process process)
            {
                _process = process;
            }

            public int ExitCode => _process.ExitCode;

            public async Task<string> ReadLineAsync(TimeSpan timeout)
            {
                Task<string?> read = _process.StandardOutput.ReadLineAsync();
                Task delay = Task.Delay(timeout);
                if (await Task.WhenAny(read, delay) != read)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the outbox worker to emit a line. Exited=" + _process.HasExited + ".");
                }

                string? line = await read;
                if (line == null)
                {
                    throw new InvalidOperationException(
                        "The outbox worker exited before emitting the expected line. stderr: " +
                        ReadAllStandardError());
                }

                return line;
            }

            public async Task WaitForExitAsync(TimeSpan timeout)
            {
                Task exit = _process.WaitForExitAsync();
                Task delay = Task.Delay(timeout);
                if (await Task.WhenAny(exit, delay) != exit)
                {
                    throw new TimeoutException("Timed out waiting for the outbox worker to exit.");
                }

                await exit;
            }

            public async Task KillAsync()
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync();
            }

            public string ReadAllStandardOutput()
            {
                return _process.StandardOutput.ReadToEnd();
            }

            public string ReadAllStandardError()
            {
                try
                {
                    return _process.StandardError.ReadToEnd();
                }
                catch (InvalidOperationException)
                {
                    return string.Empty;
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        await _process.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    _process.Dispose();
                }
            }
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "mahjong-file-outbox-process-tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
