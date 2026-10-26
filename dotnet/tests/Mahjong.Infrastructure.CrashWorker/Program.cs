using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Mahjong.TableRuntime;
using Npgsql;

internal static class Program
{
    private const string Room = "p52-crash-room";
    private const string Owner = "p52-crash-owner";
    private const string Boot = "p52-crash-boot";
    private const string Account = "p52-crash-account";
    private const string Scope = "p52-crash-scope";
    private const string Request = "p52-crash-request";
    private const string Match = "p52-crash-match";
    private const string OutboxText = "p52-crash-outbox-payload";
    private const string RuntimeVariable = "MAHJONG_P52_TEST_POSTGRES";
    private const string AdminVariable = "MAHJONG_P52_TEST_POSTGRES_ADMIN";
    private const string MarkerVariable = "MAHJONG_P52_CRASH_MARKER";

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: Mahjong.Infrastructure.CrashWorker <writer|reader|reset>");
            return 2;
        }

        string mode = args[0];
        try
        {
            switch (mode)
            {
                case "reset":
                    await ResetAsync(CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("RESET-OK");
                    return 0;
                case "writer":
                    await WriterAsync(CancellationToken.None).ConfigureAwait(false);
                    return 0;
                case "reader":
                    await ReaderAsync(CancellationToken.None).ConfigureAwait(false);
                    return 0;
                default:
                    Console.Error.WriteLine("unknown mode: " + mode);
                    return 2;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static string Require(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Missing required environment variable " + name + ".");
        }

        return value;
    }

    private static async Task ResetAsync(CancellationToken cancellationToken)
    {
        await PostgresTableMigrator.ApplyAsync(Require(AdminVariable), cancellationToken).ConfigureAwait(false);
        await using var connection = new NpgsqlConnection(Require(AdminVariable));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = @"
TRUNCATE table_store.table_recovery_audit,
         table_store.settlement_outbox,
         table_store.table_committed_facts,
         table_store.table_control_journal,
         table_store.table_command_journal,
         table_store.table_timing_journal,
         table_store.table_checkpoint,
         table_store.table_event,
         table_store.request_receipt,
         table_store.request_scope,
         table_store.table_lease,
         table_store.table_state
CASCADE";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriterAsync(CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken).ConfigureAwait(false);
        var options = new PostgresTableOptions(Require(RuntimeVariable), Require("MAHJONG_TABLE_PG_PAYLOAD_KEY"));
        await using var store = new PostgresTableStore(options);

        ulong nowUtc = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RoundStateMachine round = Dealt("round-p52-crash");
        TableCheckpoint initial = InitialCheckpoint(round, nowUtc);
        await store.CreateRoomAsync(initial, cancellationToken).ConfigureAwait(false);
        await store.IssueScopeAsync(
            new TableScopeRecord(
                new RequestScopeId(Scope),
                Account,
                TableScopeRole.Player,
                Room,
                nowUtc,
                nowUtc + 3_600_000UL),
            cancellationToken).ConfigureAwait(false);
        TableLeaseAcquireResult lease = await store.AcquireAsync(
            Room,
            Owner,
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired || lease.Lease == null)
        {
            throw new InvalidOperationException("Could not acquire the crash-worker lease.");
        }

        RoundCommand command = DiscardCommand(round);
        RoundTransition transition = round.Execute(command);
        if (!transition.IsAccepted || transition.State == null)
        {
            throw new InvalidOperationException("The crash-worker command was not accepted.");
        }

        StateVersion commandVersion = new StateVersion(1);
        var events = new List<TableEventRecord>();
        ulong seq = 0;
        for (int i = 0; i < transition.Events.Count; i++)
        {
            seq++;
            RoundEvent domainEvent = transition.Events[i];
            events.Add(new TableEventRecord(
                new EventSeq(seq),
                commandVersion,
                i,
                domainEvent.Type,
                domainEvent.Seat,
                domainEvent.Tile,
                domainEvent.Detail,
                1,
                TableEventFactCodec.Encode(commandVersion, i, transition.State.SourceFingerprint)));
        }

        byte[] statePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(transition.State));
        byte[] commandPayload = RoundCommandCodec.Encode(command);
        TablePayloadSha256 digest = TablePayloadSha256.Compute(Encoding.UTF8.GetBytes(Request + "|" + command.Action));
        var receipt = new TableDurableReceipt(
            Account,
            new RequestScopeId(Scope),
            new RequestId(Request),
            "P52_CRASH",
            digest,
            Receipt.Applied(new StateVersion(0), commandVersion, new EventSeq(seq)),
            string.Empty,
            nowUtc,
            nowUtc + 3_600_000UL);
        var journal = new TableCommandJournalRecord(
            commandVersion,
            new StateVersion(0),
            new RequestId(Request),
            Account,
            new RequestScopeId(Scope),
            "P52_CRASH",
            digest,
            commandPayload,
            RoundCommandCodec.FormatVersion);
        byte[] outboxPayload = Encoding.UTF8.GetBytes(OutboxText);
        var outbox = new TableSettlementOutboxRecord(
            Match,
            1,
            outboxPayload,
            TablePayloadSha256.Compute(outboxPayload),
            nowUtc);
        TableTimingCheckpoint timing = Timing(transition.State.RoundId, nowUtc);
        var request = new TableDurableCommitRequest(
            Room,
            Owner,
            lease.Lease.OwnerEpoch,
            new StateVersion(0),
            receipt,
            false,
            events,
            journal,
            new[] { outbox },
            null,
            statePayload,
            transition.State.SourceFingerprint,
            true,
            timing,
            2L);
        TableDurableCommitResult committed = await store.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        if (!committed.IsCommitted)
        {
            throw new InvalidOperationException("The crash-worker commit failed: " + committed.Disposition + " " + committed.Detail);
        }

        string marker = Require(MarkerVariable);
        var json = "{" +
            "\"stateVersion\":1," +
            "\"lastEventSeq\":" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"eventCount\":" + events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"requestId\":\"" + Request + "\"," +
            "\"scopeId\":\"" + Scope + "\"," +
            "\"accountId\":\"" + Account + "\"," +
            "\"matchId\":\"" + Match + "\"," +
            "\"outboxResultVersion\":1," +
            "\"outboxSha256\":\"" + TablePayloadSha256.Compute(outboxPayload).ToHex() + "\"," +
            "\"committedAtUtcMs\":" + nowUtc.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "}";
        using (var stream = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }

        // The durable commit already returned and the marker is flushed. Stay alive
        // without publishing anything until the verification wrapper kills this
        // process, then restarts PostgreSQL and runs the independent reader.
        Thread.Sleep(Timeout.Infinite);
    }

    private static async Task ReaderAsync(CancellationToken cancellationToken)
    {
        var options = new PostgresTableOptions(Require(RuntimeVariable), Require("MAHJONG_TABLE_PG_PAYLOAD_KEY"));
        await using var store = new PostgresTableStore(options);
        TableStateRow? state = await store.LoadStateAsync(Room, cancellationToken).ConfigureAwait(false);
        if (state == null)
        {
            throw new InvalidOperationException("The restored/crashed table_state row is missing.");
        }

        TableCheckpoint? checkpoint = await store.LoadCheckpointAsync(Room, cancellationToken).ConfigureAwait(false);
        if (checkpoint == null)
        {
            throw new InvalidOperationException("The restored/crashed checkpoint is missing.");
        }

        IReadOnlyList<TableEventRecord> events = await store.LoadEventsAfterAsync(
            Room,
            new EventSeq(0),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TableCommandJournalRecord> commands = await store.LoadCommandsAfterAsync(
            Room,
            new StateVersion(0),
            cancellationToken).ConfigureAwait(false);
        TableDurableReceipt? receipt = await store.TryGetAsync(
            Account,
            new RequestScopeId(Scope),
            new RequestId(Request),
            cancellationToken).ConfigureAwait(false);
        TableSettlementOutboxRecord? outbox = await store.TryGetAsync(Match, 1, cancellationToken).ConfigureAwait(false);
        if (receipt == null || outbox == null)
        {
            throw new InvalidOperationException("The persisted receipt/outbox is missing after restart.");
        }

        var recovery = new TableRecoveryService(store, store, store, new WorkerClock());
        TableRecoveryResult recovered = await recovery.RecoverAsync(
            Room,
            new TableRecoveryOptions(
                Owner,
                "p52-crash-reader-boot",
                1,
                TimeSpan.FromMinutes(10),
                0L,
                Match,
                requireCheatLedger: false,
                requireCommittedFacts: false),
            cancellationToken).ConfigureAwait(false);
        if (!recovered.Recovered)
        {
            throw new InvalidOperationException(
                "Recovery after PostgreSQL restart failed: " + recovered.ReasonCode + " " + recovered.Detail);
        }

        if (recovered.Report!.FinalStateVersion != state.StateVersion ||
            recovered.Report.FinalEventSeq != state.LastEventSeq ||
            recovered.Report.FinalStateHash != state.StateHash ||
            recovered.Report.FinalFingerprint != state.StateFingerprint)
        {
            throw new InvalidOperationException("Recovered state does not match the durable rows.");
        }

        // The normal discard round is not terminal, so recovery intentionally does
        // not enter the terminal-only outbox branch. The outbox row is still loaded
        // independently and its SHA-256 is compared by the wrapper.
        if (outbox.PayloadSha256.ToHex() != TablePayloadSha256.Compute(Encoding.UTF8.GetBytes(OutboxText)).ToHex())
        {
            throw new InvalidOperationException("Persisted settlement outbox payload changed across restart.");
        }

        string result = "{" +
            "\"stateVersion\":" + state.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"lastEventSeq\":" + state.LastEventSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"eventCount\":" + events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"commandCount\":" + commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"receiptStatus\":\"" + receipt.Receipt.Status + "\"," +
            "\"outboxPublished\":" + (outbox.PublishedAtUtcMs.HasValue ? "true" : "false") + "," +
            "\"outboxSha256\":\"" + outbox.PayloadSha256.ToHex() + "\"," +
            "\"recoveryOwnerEpoch\":" + recovered.Report.CurrentOwnerEpoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"recovered\":true" +
            "}";
        string? output = Environment.GetEnvironmentVariable("MAHJONG_P52_CRASH_RESULT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            File.WriteAllText(output, result, new UTF8Encoding(false));
        }

        Console.WriteLine("READER-OK " + result);
    }

    private static RoundStateMachine Dealt(string roundId)
    {
        var spec = new RoundSpec(
            roundId,
            0,
            1,
            0,
            0,
            new long[] { 25000, 25000, 25000, 25000 },
            0);
        TilePositionMap dealt = TilePositionMap.CreateDealt(
            RecordedRandomDraw.Standard("p52-crash-dealt-" + roundId),
            0);
        return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
    }

    private static TableCheckpoint InitialCheckpoint(RoundStateMachine round, ulong nowUtc)
    {
        TableTimingCheckpoint timing = Timing(round.RoundId, nowUtc);
        var registry = new ControlRegistry(
            new DisconnectPolicy(),
            new Dictionary<int, SeatControllerKind>
            {
                [0] = SeatControllerKind.RemoteHuman,
                [1] = SeatControllerKind.RemoteHuman,
                [2] = SeatControllerKind.RemoteHuman,
                [3] = SeatControllerKind.RemoteHuman
            });
        return TableCheckpointFactory.Create(
            Room,
            round,
            new StateVersion(0),
            new EventSeq(0),
            new TableControlCheckpoint(registry.Capture(), 0L),
            timing,
            1,
            Boot,
            nowUtc,
            cheat: null,
            committedFactsPayload: null,
            timingVersion: 1L,
            capturedMonotonicMillis: 0L,
            committedHistory: null);
    }

    private static TableTimingCheckpoint Timing(string roundId, ulong capturedAtUtcMs)
    {
        var clock = new DecisionClock(new DecisionClockConfig(1000L, 0L, ReserveScope.Round));
        clock.BeginRound(roundId, 0L);
        return TableCheckpointFactory.CaptureTiming(
            clock,
            Array.Empty<PendingDecision>(),
            0L,
            capturedAtUtcMs,
            Boot);
    }

    private static RoundCommand DiscardCommand(RoundStateMachine round)
    {
        int seat = round.CurrentSeat;
        TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
        return RoundCommand.Discard(round.RoundId, seat, tile);
    }

    private sealed class WorkerClock : ITableClock
    {
        public long MonotonicMillis => 1000L;

        public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
