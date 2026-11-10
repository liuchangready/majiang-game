using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalRecordedRoundsTests
    {
        [Fact]
        public void replay_projection_keeps_draw_timing_but_omits_private_tile_and_detail()
        {
            var draw = new TableEventRecord(new EventSeq(1), new StateVersion(1), 0,
                RoundEventType.TileDrawn, 0, new TileId(17), "private draw detail");
            var discard = new TableEventRecord(new EventSeq(2), new StateVersion(2), 0,
                RoundEventType.TileDiscarded, 0, new TileId(18), "internal detail");

            LocalRecordedEvent projectedDraw = LocalRecordedRounds.ProjectEvent(draw)!;
            Assert.Equal("TileDrawn", projectedDraw.TypeCode);
            Assert.Null(projectedDraw.PublicTile);
            LocalRecordedEvent projected = LocalRecordedRounds.ProjectEvent(discard)!;
            Assert.Equal("TileDiscarded", projected.TypeCode);
            Assert.Equal(0, projected.Seat);
            Assert.Equal((int?)18, projected.PublicTile);
            Assert.Equal((ulong)2, projected.Sequence);
        }

        [Fact]
        public async Task list_reads_real_localhost_commit_and_deduplicates_copy_by_round_identity()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            OpenedLocalTable table = await OpenReadyAsync(harness.Options, timeout.Token);
            try
            {
                await SubmitOneDiscardAsync(table.Session, timeout.Token);
                byte[] saveBytes = File.ReadAllBytes(harness.SavePath);
                string before = Convert.ToHexString(SHA256.HashData(saveBytes));
                LocalSave save = LocalSaveCodec.DecodeEnvelope(saveBytes);
                TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                string actualRoundId = RoundRecoveryCodec.Decode(checkpoint.StatePayload).Spec.RoundId;

                LocalTableHostOptions otherOptions = P41Harness.CloneOptions(harness.Options);
                otherOptions.RoomId = "other-room";
                await using (OpenedLocalTable otherTable = await OpenReadyAsync(otherOptions, timeout.Token)) { }

                IReadOnlyList<LocalRecordedRound> local = LocalRecordedRounds.List(
                    harness.Options.SaveDirectory, P41Harness.RoomId);
                LocalRecordedRound round = Assert.Single(local);
                Assert.Equal(P41Harness.RoomId, round.MatchId);
                Assert.Equal(actualRoundId, round.RoundId);
                Assert.NotEqual(round.MatchId, round.RoundId);
                Assert.Contains(round.Events, _ => true);
                Assert.Empty(LocalRecordedRounds.List(harness.Options.SaveDirectory, "another-room"));

                string copyPath = Path.Combine(harness.Options.SaveDirectory, "copy.save");
                File.WriteAllBytes(copyPath, saveBytes);
                IReadOnlyList<LocalRecordedRound> copied = LocalRecordedRounds.List(
                    harness.Options.SaveDirectory, P41Harness.RoomId);
                Assert.Single(copied);
                Assert.Equal(round.Id, copied[0].Id);
                Assert.Equal(round.RoundId, copied[0].RoundId);
                Assert.Equal(round.Events.Count, copied[0].Events.Count);
                Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(harness.SavePath))));
            }
            finally
            {
                await table.DisposeAsync();
            }
        }

        [Theory]
        [InlineData("missing-first")]
        [InlineData("missing-last")]
        [InlineData("missing-middle")]
        [InlineData("duplicate")]
        [InlineData("out-of-order")]
        [InlineData("wrong-version")]
        [InlineData("wrong-ordinal")]
        [InlineData("wrong-binding")]
        public async Task list_rejects_incomplete_or_misordered_durable_tail(string corruption)
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            OpenedLocalTable table = await OpenReadyAsync(harness.Options, timeout.Token);
            try
            {
                await SubmitOneDiscardAsync(table.Session, timeout.Token);
                byte[] originalBytes = File.ReadAllBytes(harness.SavePath);
                string saveBytesHash = Convert.ToHexString(SHA256.HashData(originalBytes));
                LocalSave source = LocalSaveCodec.DecodeEnvelope(originalBytes);
                Assert.Contains(source.EventJournals, _ => true);
                Assert.Contains(source.Operations, x => x.Kind == LocalOperationKind.Commit && x.Events.Count > 0);

                string isolatedRoot = Path.Combine(harness.Root, "corrupt-" + corruption);
                Directory.CreateDirectory(isolatedRoot);
                string savePath = Path.Combine(isolatedRoot, P41Harness.RoomId + ".save");
                LocalSave damaged = LocalSaveCodec.DecodeEnvelope(originalBytes);
                List<LocalDurableOperation> commits = damaged.Operations
                    .Where(x => x.Kind == LocalOperationKind.Commit && x.Events.Count > 0).ToList();
                if (corruption == "missing-first") commits[0].Events.RemoveAt(0);
                else if (corruption == "missing-last") commits[commits.Count - 1].Events.RemoveAt(commits[commits.Count - 1].Events.Count - 1);
                else if (corruption == "missing-middle") commits.First(x => x.Events.Count > 1).Events.RemoveAt(1);
                else if (corruption == "duplicate") commits[0].Events.Insert(0, commits[0].Events[0]);
                else if (corruption == "out-of-order")
                {
                    LocalDurableOperation withTwo = commits.First(x => x.Events.Count > 1);
                    var first = withTwo.Events[0];
                    withTwo.Events[0] = withTwo.Events[1];
                    withTwo.Events[1] = first;
                }
                else if (corruption == "wrong-version" || corruption == "wrong-ordinal" || corruption == "wrong-binding")
                {
                    LocalDurableOperation operation = commits[0];
                    TableEventRecord old = operation.Events[0];
                    byte[]? payload = old.Payload;
                    ulong commandVersion = old.CommandVersion.Value;
                    int ordinal = old.Ordinal;
                    if (corruption == "wrong-version") commandVersion++;
                    if (corruption == "wrong-ordinal") ordinal++;
                    if (corruption == "wrong-binding")
                    {
                        payload = (byte[])payload!.Clone();
                        payload[payload.Length - 1] ^= 0x01;
                    }
                    operation.Events[0] = new TableEventRecord(
                        old.Seq, new StateVersion(commandVersion), ordinal, old.Type, old.Seat,
                        old.Tile, old.Detail, old.EventSchema, payload, old.Cheat);
                }
                damaged.EventJournals = damaged.Operations
                    .Where(x => x.Kind == LocalOperationKind.Commit)
                    .SelectMany(x => x.Events).ToList();
                File.WriteAllBytes(savePath, LocalSaveCodec.EncodeEnvelope(damaged));

                Assert.Throws<InvalidDataException>(() => LocalRecordedRounds.List(isolatedRoot, P41Harness.RoomId));
                Assert.Equal(saveBytesHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(harness.SavePath))));
            }
            finally
            {
                await table.DisposeAsync();
            }
        }

        private static async Task<OpenedLocalTable> OpenReadyAsync(LocalTableHostOptions options, CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "record-replay-tests", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            try
            {
                string sessionTicket = LocalTableHost.IssueTicket(options);
                await LocalHostCommandTests.PumpUntilReadyAsync(session, sessionTicket, cancellationToken);
                return new OpenedLocalTable(channel, session);
            }
            catch
            {
                await channel.CloseAsync(new TableChannelCloseReason("RECORD_TEST_ABORT", true), CancellationToken.None);
                throw;
            }
        }

        private static async Task SubmitOneDiscardAsync(TableSession session, CancellationToken cancellationToken)
        {
            var before = session.PlayerSnapshot!;
            Assert.NotNull(before.PrivateView);
            Assert.Contains(before.PrivateView!.ClosedTiles, _ => true);
            var payload = CommandPayloadFactory.SubmitAction(
                before.RoundId, "record-replay-tests", 0UL, ProtocolActionType.DiscardTile,
                tileIds: new[] { before.PrivateView.ClosedTiles[0] });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction, payload, new RequestId("record-replay-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId, new StateVersion(before.StateVersion), new RoundId(before.RoundId),
                decisionId: null, controlEpoch: new ControlEpoch(0));
            CommandSubmission submitted = await session.SubmitAsync(command, cancellationToken);
            Assert.True(submitted.Sent);

            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested &&
                           (session.PlayerSnapshot == null || session.PlayerSnapshot.StateVersion <= before.StateVersion))
                        await session.PumpOnceAsync(pumpCts.Token);
                }
                catch (OperationCanceledException) { }
            }, cancellationToken);
            try
            {
                await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session, value => value.StateVersion > before.StateVersion,
                    "a committed replay fixture discard", pumpCts.Token);
            }
            finally
            {
                pumpCts.Cancel();
                try { await pump; } catch (OperationCanceledException) { }
            }
        }

        private sealed class OpenedLocalTable : IAsyncDisposable
        {
            private readonly ITableChannel _channel;
            private readonly TableSession _session;

            public OpenedLocalTable(ITableChannel channel, TableSession session)
            {
                _channel = channel;
                _session = session;
            }

            public TableSession Session => _session;

            public async ValueTask DisposeAsync()
            {
                await _session.CloseAsync("RECORD_TEST_DONE", CancellationToken.None);
                await _channel.CloseAsync(new TableChannelCloseReason("RECORD_TEST_DONE", true), CancellationToken.None);
            }
        }

    }
}
