using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.CrashWorker
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: <saveDir> <roomId> <markerPath> [settlement]");
                return 2;
            }

            string saveDir = args[0];
            string roomId = args[1];
            string marker = args[2];
            string mode = args.Length >= 4 ? args[3] : "action";

            var options = new LocalTableHostOptions
            {
                SaveDirectory = saveDir,
                RoomId = roomId,
                OwnerInstanceId = "crash-owner",
                AutoPlay = false
            };

            if (mode == "settlement")
            {
                var clock = SystemTableClock.Instance;
                using (LocalTableStore settleStore = await LocalTableStore.OpenAsync(options, clock, null, CancellationToken.None))
                {
                    var facts = new LocalSnapshotFacts(LocalHostEnvironment.System, clock);
                    var settleRound = facts.CreateInitialRound(options);
                    TableCheckpoint checkpoint = facts.CreateInitialCheckpoint(options, settleRound);
                    await settleStore.CreateRoomAsync(checkpoint, CancellationToken.None);

                    byte[] outboxPayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
                    var outboxHash = TablePayloadSha256.Compute(outboxPayload);
                    var outboxRecord = new TableSettlementOutboxRecord(
                        "match-settle-1",
                        1,
                        outboxPayload,
                        outboxHash,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        publishedAtUtcMs: null);

                    TableLeaseSnapshot? lease = await settleStore.GetAsync(roomId, CancellationToken.None);
                    TableStateRow? stateRow = await settleStore.LoadStateAsync(roomId, CancellationToken.None);

                    var timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                    var cmdEvent = new TableEventRecord(
                        new EventSeq(1),
                        new StateVersion(1),
                        0,
                        RoundEventType.RoundEnded,
                        0,
                        null,
                        "settle",
                        1,
                        Array.Empty<byte>());

                    var cmdJournal = new TableCommandJournalRecord(
                        new StateVersion(1),
                        stateRow!.StateVersion,
                        new RequestId("crash-settle-req"),
                        "local-" + roomId + "-seat0",
                        new RequestScopeId("crash-settle-scope"),
                        "DECLARE_TSUMO",
                        outboxHash,
                        outboxPayload);

                    var commit = new TableDurableCommitRequest(
                        roomId,
                        options.OwnerInstanceId,
                        lease!.OwnerEpoch,
                        stateRow.StateVersion,
                        receipt: null,
                        internalCommand: true,
                        events: new[] { cmdEvent },
                        commandJournal: cmdJournal,
                        outbox: new[] { outboxRecord },
                        checkpoint: null,
                        nextStatePayload: checkpoint.StatePayload,
                        nextStateFingerprint: checkpoint.StateFingerprint == 0 ? 12345UL : checkpoint.StateFingerprint,
                        terminal: true,
                        timing: timing,
                        timingVersion: checkpoint.TimingVersion + 1L);

                    TableDurableCommitResult result = await settleStore.CommitAsync(commit, CancellationToken.None);
                    if (!result.IsCommitted)
                    {
                        Console.Error.WriteLine("Settlement commit failed: " + result.ReasonCode + " " + result.Detail);
                        return 6;
                    }

                    File.WriteAllLines(marker, new[]
                    {
                        "match-settle-1",
                        "1",
                        outboxHash.ToHex(),
                        Convert.ToHexString(outboxPayload),
                        "unpublished"
                    });

                    // Real kill without dispose
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return 0;
                }
            }

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(options);
            string channelTicket = LocalTableHost.IssueTicket(options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "crash-worker", null), CancellationToken.None);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "crash-worker"), CancellationToken.None);
            for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(CancellationToken.None);
            }

            if (session.State != TableConnectionState.Ready)
            {
                Console.Error.WriteLine("worker not ready: " + session.LastError);
                return 3;
            }

            PlayerSnapshot baseline = session.PlayerSnapshot!;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                baseline.RoundId,
                "local-direct",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { baseline.PrivateView.ClosedTiles[0] });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("crash-request-1"),
                session.Binding!.Scope.ScopeId,
                new StateVersion(baseline.StateVersion),
                new RoundId(baseline.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            CommandSubmission submission = await session.SubmitAsync(command, CancellationToken.None);
            if (!submission.Sent)
            {
                Console.Error.WriteLine("worker command not sent");
                return 4;
            }

            // Wait until the commit is observable; then die without any dispose/close path.
            for (int i = 0; i < 200 && session.PlayerSnapshot!.StateVersion == baseline.StateVersion; i++)
            {
                await Task.Delay(10);
                if (i % 2 == 0 && session.State == TableConnectionState.Ready)
                {
                    try
                    {
                        await session.PumpOnceAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            if (session.PlayerSnapshot!.StateVersion == baseline.StateVersion)
            {
                Console.Error.WriteLine("worker commit not observed");
                return 5;
            }

            for (int i = 0; i < 500 && session.PendingCommands.Count > 0; i++)
            {
                if (session.State == TableConnectionState.Ready)
                {
                    try
                    {
                        await session.PumpOnceAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                }

                await Task.Delay(10);
            }

            var localChannel = channel as LocalTableChannel;
            var runtimeField = typeof(LocalTableChannel).GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance);
            var runtime = runtimeField?.GetValue(localChannel);
            var driverField = runtime?.GetType().GetField("_driver", BindingFlags.NonPublic | BindingFlags.Instance);
            var driver = driverField?.GetValue(runtime) as LocalTableDriver;
            if (driver != null)
            {
                await driver.StopAsync();
            }

            var actorField = runtime?.GetType().GetField("_actor", BindingFlags.NonPublic | BindingFlags.Instance);
            var actor = actorField?.GetValue(runtime) as TableActor;
            var storeField = runtime?.GetType().GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance);
            var store = storeField?.GetValue(runtime) as LocalTableStore;

            var saveField = typeof(LocalTableStore).GetField("_save", BindingFlags.NonPublic | BindingFlags.Instance);
            var save = saveField?.GetValue(store) as LocalSave;

            TilePositionMap? tiles = null;
            if (save != null)
            {
                for (int i = save.Operations.Count - 1; i >= 0; i--)
                {
                    if (save.Operations[i].NextStatePayload != null)
                    {
                        RoundRecoveryState roundState = RoundRecoveryCodec.Decode(save.Operations[i].NextStatePayload!);
                        tiles = roundState.Tiles;
                        break;
                    }
                }

                if (tiles == null && save.BaseCheckpointBlob != null)
                {
                    TableCheckpoint cpBase = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                    RoundRecoveryState roundState = RoundRecoveryCodec.Decode(cpBase.StatePayload);
                    tiles = roundState.Tiles;
                }
            }

            if (tiles == null)
            {
                var round = actor?.GetRoundStateForHost();
                tiles = round?.Tiles;
            }

            var expectedDigest = TablePayloadSha256.Compute(payload.CopyBytes());

            TableCheckpoint? cp = store != null ? await store.LoadCheckpointAsync(roomId, CancellationToken.None) : null;
            TableCheatLedgerCheckpoint? cheatCp = cp?.CheatPayload != null ? TableCheatLedgerCheckpointCodec.Decode(cp.CheatPayload) : null;
            long b0 = cheatCp?.Balances.Count > 0 ? cheatCp.Balances[0] : 0L;
            long b1 = cheatCp?.Balances.Count > 1 ? cheatCp.Balances[1] : 0L;
            long b2 = cheatCp?.Balances.Count > 2 ? cheatCp.Balances[2] : 0L;
            long b3 = cheatCp?.Balances.Count > 3 ? cheatCp.Balances[3] : 0L;

            TableStateRow? authorState = store != null ? await store.LoadStateAsync(roomId, CancellationToken.None) : null;
            ulong authorVersion = authorState?.StateVersion.Value ?? session.PlayerSnapshot.StateVersion;

            var lines = new List<string>
            {
                session.Binding!.Scope.ScopeId.Value,
                "crash-request-1",
                authorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "local-" + roomId + "-seat0",
                expectedDigest.ToHex(),
                $"{b0},{b1},{b2},{b3}",
                tiles != null ? TilePositionMap.Validate(tiles).StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0"
            };

            if (tiles != null)
            {
                for (int i = 0; i < 136; i++)
                {
                    TilePosition pos = tiles[i];
                    lines.Add($"{i}:{(int)pos.Zone}:{pos.Index}");
                }
            }

            File.WriteAllLines(marker, lines);

            // Real crash: no actor close, no channel close, no store dispose.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return 0;
        }
    }
}
