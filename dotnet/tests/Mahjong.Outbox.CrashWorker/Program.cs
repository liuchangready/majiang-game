using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.Outbox.CrashWorker
{
    /// <summary>
    /// Test-only process driver. Every command operates on a real
    /// <see cref="FileOutboxStore"/> in a fresh process; the test parent kills
    /// this process at deterministic commit phases to verify crash boundaries.
    /// </summary>
    internal static class Program
    {
        private const string AccountId = "account-1";
        private const string ScopeId = "scope-1";
        private const string SessionId = "session-1";
        private const string RoomId = "room-1";

        public static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: <command> <directory> [request-id] [phase]");
                return 64;
            }

            string command = args[0];
            string directory = args[1];
            string requestId = args.Length >= 3 ? args[2] : "request-1";

            try
            {
                switch (command)
                {
                    case "save":
                        await SaveAsync(directory, requestId, hold: false).ConfigureAwait(false);
                        return 0;
                    case "save-hold":
                        await SaveAsync(directory, requestId, hold: true).ConfigureAwait(false);
                        return 0;
                    case "save-wait":
                        Console.Out.WriteLine("READY");
                        Console.Out.Flush();
                        Thread.Sleep(TimeSpan.FromMinutes(2));
                        return 0;
                    case "save-block":
                    {
                        if (args.Length < 4)
                        {
                            Console.Error.WriteLine("save-block requires a phase.");
                            return 64;
                        }

                        FileOutboxWritePhase phase = ParsePhase(args[3]);
                        InstallBlockingObserver(phase);
                        Console.Out.WriteLine("READY");
                        Console.Out.Flush();
                        await SaveAsync(directory, requestId, hold: false).ConfigureAwait(false);
                        return 0;
                    }

                    case "save-mismatch":
                    {
                        var store = new FileOutboxStore(directory);
                        OutboxEntry entry = BuildEntry(requestId, new uint[] { 5, 7 });
                        try
                        {
                            await store.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
                            Console.Out.WriteLine("UNEXPECTED_SAVE");
                            return 4;
                        }
                        catch (InvalidOperationException ex)
                        {
                            Console.Out.WriteLine("CONFLICT " + ex.Message);
                            return 0;
                        }
                    }

                    case "save-after-resolve":
                    {
                        var store = new FileOutboxStore(directory);
                        OutboxEntry entry = BuildEntry(requestId, new uint[] { 5, 6 });
                        try
                        {
                            await store.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
                            Console.Out.WriteLine("UNEXPECTED_SAVE");
                            return 4;
                        }
                        catch (InvalidOperationException ex) when (ex.Message.IndexOf("already resolved", StringComparison.Ordinal) >= 0)
                        {
                            Console.Out.WriteLine("RESOLVED");
                            return 0;
                        }
                    }

                    case "resolve":
                        await ResolveAsync(directory, requestId, hold: false, blockPhase: null).ConfigureAwait(false);
                        return 0;
                    case "resolve-hold":
                        await ResolveAsync(directory, requestId, hold: true, blockPhase: null).ConfigureAwait(false);
                        return 0;
                    case "resolve-wait":
                    {
                        var store = new FileOutboxStore(directory);
                        IReadOnlyList<OutboxEntry> entries = await store.LoadUnresolvedAsync(CancellationToken.None).ConfigureAwait(false);
                        OutboxEntry? entry = entries.FirstOrDefault(e => string.Equals(e.RequestId.Value, requestId, StringComparison.Ordinal));
                        if (entry == null)
                        {
                            Console.Out.WriteLine("MISSING " + requestId);
                            return 3;
                        }

                        Console.Out.WriteLine("READY " + requestId);
                        Console.Out.Flush();
                        Thread.Sleep(TimeSpan.FromMinutes(2));
                        return 0;
                    }

                    case "resolve-block":
                    {
                        if (args.Length < 4)
                        {
                            Console.Error.WriteLine("resolve-block requires a phase.");
                            return 64;
                        }

                        FileOutboxWritePhase phase = ParsePhase(args[3]);
                        await ResolveAsync(directory, requestId, hold: false, blockPhase: phase).ConfigureAwait(false);
                        return 0;
                    }

                    case "load":
                    {
                        var store = new FileOutboxStore(directory);
                        IReadOnlyList<OutboxEntry> entries = await store.LoadUnresolvedAsync(CancellationToken.None).ConfigureAwait(false);
                        Console.Out.WriteLine("COUNT " + entries.Count.ToString(CultureInfo.InvariantCulture));
                        for (int i = 0; i < entries.Count; i++)
                        {
                            OutboxEntry entry = entries[i];
                            byte[] payload = entry.CopyCanonicalPayload();
                            Console.Out.WriteLine(
                                "ENTRY " +
                                entry.RequestId.Value + " " +
                                entry.ScopeId.Value + " " +
                                entry.Key.AccountId + " " +
                                ((int)entry.Kind).ToString(CultureInfo.InvariantCulture) + " " +
                                entry.PayloadSchemaVersion.ToString(CultureInfo.InvariantCulture) + " " +
                                entry.ExpectedStateVersion.Value.ToString(CultureInfo.InvariantCulture) + " " +
                                (entry.RoundId.HasValue ? entry.RoundId.Value.Value : "-") + " " +
                                (entry.DecisionId.HasValue ? entry.DecisionId.Value.Value : "-") + " " +
                                (entry.ControlEpoch.HasValue ? entry.ControlEpoch.Value.Value.ToString(CultureInfo.InvariantCulture) : "-") + " " +
                                (entry.OwnerEpoch.HasValue ? entry.OwnerEpoch.Value.Value.ToString(CultureInfo.InvariantCulture) : "-") + " " +
                                entry.SessionId.Value + " " +
                                entry.RoomId.Value + " " +
                                entry.DigestSha256 + " " +
                                payload.Length.ToString(CultureInfo.InvariantCulture) + " " +
                                Convert.ToBase64String(payload));
                        }

                        Console.Out.Flush();
                        return 0;
                    }

                    default:
                        Console.Error.WriteLine("unknown worker command " + command);
                        return 64;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 70;
            }
        }

        private static async Task SaveAsync(string directory, string requestId, bool hold)
        {
            var store = new FileOutboxStore(directory);
            OutboxEntry entry = BuildEntry(requestId, new uint[] { 5, 6 });
            await store.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
            Console.Out.WriteLine(
                "SAVED " + entry.RequestId.Value + " " + entry.DigestSha256 + " " +
                entry.PayloadLength.ToString(CultureInfo.InvariantCulture));
            Console.Out.Flush();
            if (hold)
            {
                Thread.Sleep(TimeSpan.FromMinutes(2));
            }
        }

        private static async Task ResolveAsync(string directory, string requestId, bool hold, FileOutboxWritePhase? blockPhase)
        {
            var store = new FileOutboxStore(directory);
            IReadOnlyList<OutboxEntry> entries = await store.LoadUnresolvedAsync(CancellationToken.None).ConfigureAwait(false);
            OutboxEntry? entry = entries.FirstOrDefault(e => string.Equals(e.RequestId.Value, requestId, StringComparison.Ordinal));
            if (entry == null)
            {
                // A resolve-hold caller may start from an empty store.
                entry = BuildEntry(requestId, new uint[] { 5, 6 });
                try
                {
                    await store.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Already resolved: the caller is testing post-resolve
                    // restart behaviour and there is nothing left to do.
                    Console.Out.WriteLine("RESOLVED " + requestId);
                    Console.Out.Flush();
                    if (hold)
                    {
                        Thread.Sleep(TimeSpan.FromMinutes(2));
                    }

                    return;
                }
            }

            if (blockPhase.HasValue)
            {
                InstallBlockingObserver(blockPhase.Value);
                Console.Out.WriteLine("READY " + requestId);
                Console.Out.Flush();
            }

            OutboxResolution resolution = OutboxResolution.FromReceipt(
                ReceiptStatus.Applied,
                null,
                new StateVersion(5),
                new StateVersion(6),
                new EventSeq(11),
                1234,
                entry.DigestSha256,
                fromLookup: false);
            await store.ResolveAsync(entry.Key, resolution, CancellationToken.None).ConfigureAwait(false);
            Console.Out.WriteLine("RESOLVED " + requestId);
            Console.Out.Flush();
            if (hold)
            {
                Thread.Sleep(TimeSpan.FromMinutes(2));
            }
        }

        private static void InstallBlockingObserver(FileOutboxWritePhase target)
        {
            FileOutboxFaultInjection.Observer = (phase, path) =>
            {
                if (phase == target)
                {
                    Console.Out.WriteLine("PHASE " + phase + " " + path);
                    Console.Out.Flush();
                    Thread.Sleep(Timeout.Infinite);
                }
            };
        }

        private static FileOutboxWritePhase ParsePhase(string value)
        {
            if (!Enum.TryParse(value, ignoreCase: false, out FileOutboxWritePhase phase))
            {
                throw new ArgumentException("Unknown commit phase " + value + ".");
            }

            return phase;
        }

        private static OutboxEntry BuildEntry(string requestId, uint[] tileIds)
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                7,
                ProtocolActionType.DiscardTile,
                tileIds: tileIds);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId(ScopeId),
                new StateVersion(5),
                new RoundId("round-1"),
                new DecisionId("decision-1"),
                new ControlEpoch(7),
                new OwnerEpoch(3));
            var scope = new RequestScope(
                new RequestScopeId(ScopeId),
                AccountId,
                ProtocolRole.Player,
                new RoomId(RoomId),
                1000,
                2000);
            return new OutboxEntry(command, scope, new SessionId(SessionId), new RoomId(RoomId));
        }
    }
}
