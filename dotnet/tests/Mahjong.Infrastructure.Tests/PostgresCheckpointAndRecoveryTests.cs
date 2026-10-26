using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresCheckpointAndRecoveryTests
    {
        [Fact]
        public async Task CheckpointAndTimingFences_RoundTripAndRejectStaleVersions()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableTimingCheckpoint timing2 = P52Fixture.Timing(
                fixture.Round.RoundId,
                P52Fixture.Boot,
                now,
                fixture.Clock.Now);
            TableCheckpoint checkpoint = TableCheckpointFactory.Create(
                P52Fixture.Room,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                P52Fixture.Control(),
                timing2,
                1,
                P52Fixture.Boot,
                now,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 2L,
                capturedMonotonicMillis: fixture.Clock.Now,
                committedHistory: null);
            var fence = new TableCheckpointFence(
                P52Fixture.Room,
                P52Fixture.Owner,
                fixture.Lease.OwnerEpoch,
                new StateVersion(0),
                0L,
                1L);
            TableDurableCommitResult saved = await fixture.Store.SaveCheckpointAsync(
                checkpoint,
                fence,
                TestContext.Current.CancellationToken);
            Assert.True(saved.IsCommitted, saved.Detail);

            TableCheckpoint? loaded = await fixture.Store.LoadCheckpointAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal(2L, loaded!.TimingVersion);
            Assert.Equal(checkpoint.StatePayloadSha256, loaded.StatePayloadSha256);

            TableDurableCommitResult staleFence = await fixture.Store.SaveCheckpointAsync(
                checkpoint,
                fence,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Failed, staleFence.Disposition);
            Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleFence.ReasonCode);

            TableTimingCheckpoint timing3 = P52Fixture.Timing(
                fixture.Round.RoundId,
                P52Fixture.Boot,
                now + 1000UL,
                fixture.Clock.Now + 1000L);
            TableDurableCommitResult appended = await fixture.Store.AppendTimingAsync(
                new TableTimingAppendRequest(
                    P52Fixture.Room,
                    P52Fixture.Owner,
                    fixture.Lease.OwnerEpoch,
                    new StateVersion(0),
                    0L,
                    2L,
                    timing3),
                TestContext.Current.CancellationToken);
            Assert.True(appended.IsCommitted, appended.Detail);

            TableTimingJournalRecord? timing = await fixture.Store.LoadTimingAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.NotNull(timing);
            Assert.Equal(3L, timing!.TimingVersion);
            Assert.Equal(timing3.BootId, timing.BootId);
        }

        [Fact]
        public async Task ControlJournal_AdvancesControlVersionWithoutStateVersion()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableTimingCheckpoint timing2 = P52Fixture.Timing(
                fixture.Round.RoundId,
                P52Fixture.Boot,
                now,
                fixture.Clock.Now);
            TableCheckpoint checkpoint = TableCheckpointFactory.Create(
                P52Fixture.Room,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                P52Fixture.Control(1L),
                timing2,
                1,
                P52Fixture.Boot,
                now,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 2L,
                capturedMonotonicMillis: fixture.Clock.Now,
                committedHistory: null);
            var change = new TableControlEvent(
                1L,
                fixture.Round.RoundId,
                0,
                TableControlChangeKind.Disconnect,
                null,
                null,
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.RemoteHuman,
                ConnectionStatus.Connected,
                ConnectionStatus.Disconnected,
                null,
                fixture.Clock.Now,
                checked((long)now),
                "p52 control journal test");
            var request = new TableControlCommitRequest(
                P52Fixture.Room,
                fixture.Lease.OwnerEpoch,
                0L,
                1L,
                new StateVersion(0),
                fixture.Round.RoundId,
                change,
                P52Fixture.Owner);
            TableDurableCommitResult committed = await fixture.Store.CommitControlAsync(
                request,
                checkpoint,
                TestContext.Current.CancellationToken);
            Assert.True(committed.IsCommitted, committed.Detail);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(0), state!.StateVersion);
            Assert.Equal(1L, state.ControlVersion);
            Assert.Equal(2L, state.TimingVersion);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_control_journal",
                TestContext.Current.CancellationToken));
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));

            TableTimingCheckpoint timing3 = P52Fixture.Timing(
                fixture.Round.RoundId,
                P52Fixture.Boot,
                now + 1000UL,
                fixture.Clock.Now + 1000L);
            TableCheckpoint checkpoint2 = TableCheckpointFactory.Create(
                P52Fixture.Room,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                P52Fixture.Control(1L),
                timing3,
                1,
                P52Fixture.Boot,
                now + 1000UL,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 3L,
                capturedMonotonicMillis: fixture.Clock.Now + 1000L,
                committedHistory: null);
            var staleRequest = new TableControlCommitRequest(
                P52Fixture.Room,
                fixture.Lease.OwnerEpoch,
                0L,
                1L,
                new StateVersion(0),
                fixture.Round.RoundId,
                new TableControlEvent(
                    1L,
                    fixture.Round.RoundId,
                    0,
                    TableControlChangeKind.Reconnect,
                    null,
                    null,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    ConnectionStatus.Disconnected,
                    ConnectionStatus.Connected,
                    null,
                    fixture.Clock.Now,
                    checked((long)now + 1000),
                    "stale control"),
                P52Fixture.Owner);
            TableDurableCommitResult stale = await fixture.Store.CommitControlAsync(
                staleRequest,
                checkpoint2,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Failed, stale.Disposition);
            Assert.Equal(TableRuntimeReasonCode.StaleControlVersion, stale.ReasonCode);
        }

        [Fact]
        public async Task Recovery_ValidRoundAndOutbox()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-recovery-valid",
                terminal: true,
                outboxMatchId: "match-recovery",
                outboxResultVersion: 1,
                outboxPayload: "recovery-outbox-payload");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            TableRecoveryResult result = await RecoverAsync(fixture, "match-recovery");
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.Equal(new StateVersion(1), result.Report!.FinalStateVersion);
            Assert.Equal(1, result.Report.ReplayedCommands);
            // A normal discard round is not terminal. Recovery therefore does not enter
            // the terminal-only outbox branch; the independently committed outbox is
            // still present and must round-trip through the durable port.
            Assert.False(result.Report.OutboxPresent);
            TableSettlementOutboxRecord? outbox = await fixture.Store.TryGetAsync(
                "match-recovery",
                1,
                TestContext.Current.CancellationToken);
            Assert.NotNull(outbox);
            Assert.Equal("recovery-outbox-payload", System.Text.Encoding.UTF8.GetString(outbox!.Payload));
            Assert.Equal(new OwnerEpoch(2), result.Report.CurrentOwnerEpoch);
        }

        [Fact]
        public async Task Recovery_RejectsEventGapInsteadOfSkipping()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-gap");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "DELETE FROM table_store.table_event WHERE room_id = @room AND seq = 1",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.NotEqual(string.Empty, result.Detail);
        }

        [Fact]
        public async Task Recovery_RejectsCorruptCheckpointHash()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-corrupt-checkpoint");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_checkpoint SET state_sha256 = decode(repeat('00', 32), 'hex') WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Recovery_RejectsCorruptEventBlob()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-corrupt-event");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_event SET event_blob = decode('00', 'hex') WHERE room_id = @room AND seq = 1",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Recovery_RejectsMissingCommand()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-missing-command");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "DELETE FROM table_store.table_command_journal WHERE room_id = @room AND command_version = 1",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Recovery_RejectsTamperedStateFingerprint()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-tampered-state");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_state SET state_fingerprint = state_fingerprint + 1 WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Recovery_RejectsDuplicatedTileConservation()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate commit = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-tile-conservation");
            Assert.True((await fixture.Store.CommitAsync(
                commit.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            byte[] original = await ReadCheckpointStateBlobAsync();
            byte[] plaintext;
            using (var protector = new PostgresPayloadProtector(
                       Convert.FromBase64String(PostgresTableTestEnvironment.PayloadKey)))
            {
                plaintext = protector.Open(
                    original,
                    "mahjong-table-store|1|checkpoint-state|" + P52Fixture.Room + "|0");
                byte[] tampered = DuplicateOneTileId(plaintext);
                byte[] resealed = protector.Seal(
                    tampered,
                    "mahjong-table-store|1|checkpoint-state|" + P52Fixture.Room + "|0");
                await using var connection = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = new NpgsqlCommand(
                    @"UPDATE table_store.table_checkpoint
                         SET state_blob = @blob, state_sha256 = @hash
                       WHERE room_id = @room AND state_version = 0",
                    connection);
                command.Parameters.AddWithValue("blob", resealed);
                command.Parameters.AddWithValue("hash", Mahjong.TableRuntime.TablePayloadSha256.Compute(tampered).ToArray());
                command.Parameters.AddWithValue("room", P52Fixture.Room);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            TableRecoveryResult result = await RecoverAsync(fixture);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.NotEqual(string.Empty, result.Detail);
        }

        private static async Task<byte[]> ReadCheckpointStateBlobAsync()
        {
            await using var connection = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT state_blob FROM table_store.table_checkpoint WHERE room_id = @room AND state_version = 0",
                connection);
            command.Parameters.AddWithValue("room", P52Fixture.Room);
            object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return value as byte[] ?? throw new InvalidOperationException("Checkpoint state blob is missing.");
        }

        private static byte[] DuplicateOneTileId(byte[] payload)
        {
            for (int offset = 0; offset + 4 + (136 * 8) <= payload.Length; offset++)
            {
                if (BitConverter.ToInt32(payload, offset) != 136)
                {
                    continue;
                }

                var seen = new HashSet<int>();
                bool valid = true;
                for (int tile = 0; tile < 136; tile++)
                {
                    int record = offset + 4 + (tile * 8);
                    int id = BitConverter.ToInt32(payload, record);
                    int zone = payload[record + 4];
                    sbyte seat = unchecked((sbyte)payload[record + 5]);
                    if (id < 0 || id > 135 || zone > 10 || seat < -1 || seat > 3 || !seen.Add(id))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    continue;
                }

                int after = offset + 4 + (136 * 8);
                int wallPointer = BitConverter.ToInt32(payload, after);
                int wallEnd = BitConverter.ToInt32(payload, after + 4);
                int rinshan = BitConverter.ToInt32(payload, after + 8);
                int revision = BitConverter.ToInt32(payload, after + 12);
                int kan = BitConverter.ToInt32(payload, after + 16);
                int dora = BitConverter.ToInt32(payload, after + 20);
                if (wallPointer < 0 || wallPointer > 136 ||
                    wallEnd < 0 || wallEnd > 136 ||
                    rinshan < 0 || rinshan > 136 ||
                    revision < 0 || kan < 0 || kan > 4 || dora < 0 || dora > 5)
                {
                    continue;
                }

                var bad = (byte[])payload.Clone();
                int duplicate = BitConverter.ToInt32(payload, offset + 4 + 8);
                Array.Copy(BitConverter.GetBytes(duplicate), 0, bad, offset + 4, 4);
                return bad;
            }

            throw new InvalidOperationException("Could not locate the 136-tile table in the recovery payload.");
        }

        private static ValueTask<TableRecoveryResult> RecoverAsync(P52Fixture fixture, string? matchId = null)
        {
            var service = new TableRecoveryService(
                fixture.Store,
                fixture.Store,
                fixture.Store,
                fixture.Clock);
            return service.RecoverAsync(
                P52Fixture.Room,
                new TableRecoveryOptions(
                    P52Fixture.Owner,
                    "p52-recovery-boot",
                    1,
                    TimeSpan.FromMinutes(10),
                    0L,
                    matchId,
                    requireCheatLedger: false,
                    requireCommittedFacts: false),
                TestContext.Current.CancellationToken);
        }
    }
}
