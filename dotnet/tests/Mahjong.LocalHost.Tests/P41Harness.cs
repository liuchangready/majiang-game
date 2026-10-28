using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost.Tests
{
    internal sealed class ManualClock : ITableClock
    {
        public long MonotonicMillis { get; set; } = 1_000L;

        public long UtcNowMs { get; set; } = 1_700_000_000_000L;
    }

    internal sealed class DeterministicEnvironment
    {
        private ulong _state = 0x1234_5678_9ABC_DEF0UL;
        private int _id;

        public ManualClock Clock { get; } = new ManualClock();

        public LocalHostEnvironment Create()
        {
            return new LocalHostEnvironment
            {
                Clock = Clock,
                NextInt = bound =>
                {
                    _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
                    return (int)((_state >> 33) % (ulong)bound);
                },
                NextDouble = () =>
                {
                    _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
                    return (_state >> 11) / (double)(1UL << 53);
                },
                NewId = () =>
                {
                    _id++;
                    return "loc-" + _id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            };
        }
    }

    internal sealed class P41Harness : IAsyncDisposable
    {
        public const string RoomId = "room-1";

        private readonly string _root;

        public P41Harness()
        {
            _root = Path.Combine(
                FindRepositoryRoot(),
                "build",
                "p41-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment = new DeterministicEnvironment();
            Options = new LocalTableHostOptions
            {
                SaveDirectory = Path.Combine(_root, "local"),
                RoomId = RoomId,
                RulesetVersion = 1,
                OwnerInstanceId = "owner-local",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = Environment.Create()
            };
            Clock = Environment.Clock;
        }

        public DeterministicEnvironment Environment { get; }

        public ManualClock Clock { get; }

        public LocalTableHostOptions Options { get; }

        public string Root => _root;

        public string SavePath => Path.Combine(Options.SaveDirectory, RoomId + ".save");

        public string BackupPath => Path.Combine(Options.SaveDirectory, RoomId + ".save.prev");

        public async Task<ITableChannel> OpenChannelAsync(CancellationToken cancellationToken = default)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(Options, cancellationToken);
            string ticket = LocalTableHost.IssueTicket(Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "p41-test", null), cancellationToken);
            return channel;
        }

        public static LocalTableHostOptions CloneOptions(LocalTableHostOptions source)
        {
            return new LocalTableHostOptions
            {
                SaveDirectory = source.SaveDirectory,
                RoomId = source.RoomId,
                RulesetVersion = source.RulesetVersion,
                OwnerInstanceId = source.OwnerInstanceId,
                LeaseDuration = source.LeaseDuration,
                AutoFlushInterval = source.AutoFlushInterval,
                Environment = source.Environment
            };
        }

        public static string FindRepositoryRoot()
        {
            string? current = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "dotnet", "Mahjong.sln")))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            throw new InvalidOperationException("Repository root was not found from " + AppContext.BaseDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                // Evidence/diagnostic directories are cleaned best effort.
            }
        }
    }
}
