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
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireLookupStatus = Mahjong.Contracts.Wire.LookupStatus;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;

namespace Mahjong.Client.Tests
{
    public sealed class FileOutboxStoreTests
    {
        [Fact]
        public async Task SaveThenRestartPreservesExactBytesAndEveryMetadataField()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry expected = Entry(
                    requestId: "request-9",
                    accountId: "account-9",
                    scopeId: "scope-9",
                    sessionId: "session-9",
                    roomId: "room-9",
                    expectedStateVersion: 123,
                    roundId: "round-9",
                    decisionId: "decision-9",
                    controlEpoch: 77,
                    ownerEpoch: 33,
                    issuedAtMs: 11,
                    expiresAtMs: 999,
                    tileIds: new uint[] { 0, 7, 135 });

                await store.SaveAsync(expected, CancellationToken.None);

                var reopened = new FileOutboxStore(directory.Path);
                IReadOnlyList<OutboxEntry> loaded = await reopened.LoadUnresolvedAsync(CancellationToken.None);

                OutboxEntry actual = Assert.Single(loaded);
                Assert.Equal(expected.Key, actual.Key);
                Assert.Equal("account-9", actual.Key.AccountId);
                Assert.Equal("scope-9", actual.Key.ScopeId.Value);
                Assert.Equal("request-9", actual.Key.RequestId.Value);
                Assert.Equal(CommandKind.SubmitAction, actual.Kind);
                Assert.Equal((ushort)1, actual.PayloadSchemaVersion);
                Assert.Equal(new StateVersion(123), actual.ExpectedStateVersion);
                Assert.Equal(new RoundId("round-9"), actual.RoundId);
                Assert.Equal(new DecisionId("decision-9"), actual.DecisionId);
                Assert.Equal(new ControlEpoch(77), actual.ControlEpoch);
                Assert.Equal(new OwnerEpoch(33), actual.OwnerEpoch);
                Assert.Equal(expected.DigestSha256, actual.DigestSha256);
                Assert.Equal(expected.PayloadLength, actual.PayloadLength);
                Assert.Equal(new SessionId("session-9"), actual.SessionId);
                Assert.Equal(new RoomId("room-9"), actual.RoomId);
                Assert.Equal(expected.CopyCanonicalPayload(), actual.CopyCanonicalPayload());
                Assert.Equal(ProtocolRole.Player, actual.OwnerScope.Role);
                Assert.Equal("account-9", actual.OwnerScope.AccountId);
                Assert.Equal("scope-9", actual.OwnerScope.ScopeId.Value);
                Assert.Equal("room-9", actual.OwnerScope.RoomId.Value);
                Assert.Equal(11ul, actual.OwnerScope.IssuedAtMs);
                Assert.Equal(999ul, actual.OwnerScope.ExpiresAtMs);
                Assert.Equal(expected.CopyCanonicalPayload(), actual.ToProtocolCommand().Payload.CopyBytes());
            }
        }

        [Fact]
        public async Task SameKeySamePayloadIsIdempotentAndDoesNotDuplicate()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();

                await store.SaveAsync(entry, CancellationToken.None);
                await store.SaveAsync(entry, CancellationToken.None);

                Assert.Single(await store.LoadUnresolvedAsync(CancellationToken.None));

                var reopened = new FileOutboxStore(directory.Path);
                Assert.Single(await reopened.LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task SameKeyDifferentPayloadOrMetadataIsAConflict()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry original = Entry(tileIds: new uint[] { 5, 6 });
                await store.SaveAsync(original, CancellationToken.None);

                OutboxEntry differentBytes = Entry(tileIds: new uint[] { 5, 7 });
                InvalidOperationException bytesError = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => store.SaveAsync(differentBytes, CancellationToken.None));
                Assert.Contains("different bytes or metadata", bytesError.Message, StringComparison.Ordinal);

                OutboxEntry differentMetadata = Entry(controlEpoch: 99);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => store.SaveAsync(differentMetadata, CancellationToken.None));

                IReadOnlyList<OutboxEntry> loaded = await store.LoadUnresolvedAsync(CancellationToken.None);
                Assert.Single(loaded);
                Assert.Equal(original.CopyCanonicalPayload(), loaded[0].CopyCanonicalPayload());

                await store.SaveAsync(original, CancellationToken.None);
            }
        }

        [Fact]
        public async Task ResolutionSurvivesRestartAndPreventsResurrection()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution resolution = AppliedResolution(entry, fromLookup: true);
                await store.ResolveAsync(entry.Key, resolution, CancellationToken.None);

                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));

                var reopened = new FileOutboxStore(directory.Path);
                Assert.Empty(await reopened.LoadUnresolvedAsync(CancellationToken.None));

                await reopened.ResolveAsync(entry.Key, resolution, CancellationToken.None);

                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => reopened.SaveAsync(entry, CancellationToken.None));
                Assert.Contains("already resolved", error.Message, StringComparison.Ordinal);
                Assert.Empty(await reopened.LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task DifferentResolutionForAResolvedKeyIsRejected()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                await store.ResolveAsync(entry.Key, AppliedResolution(entry, fromLookup: false), CancellationToken.None);

                OutboxResolution rejected = OutboxResolution.FromReceipt(
                    ReceiptStatus.Rejected,
                    "STALE_DECISION",
                    new StateVersion(5),
                    null,
                    null,
                    null,
                    entry.DigestSha256,
                    fromLookup: false);

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => store.ResolveAsync(entry.Key, rejected, CancellationToken.None));
            }
        }

        [Fact]
        public async Task MissingCommittedFileIsAnEmptyStore()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.False(File.Exists(store.StorePath));
            }
        }

        [Theory]
        [InlineData("magic")]
        [InlineData("version")]
        [InlineData("length")]
        [InlineData("checksum")]
        [InlineData("truncated")]
        [InlineData("trailing")]
        public async Task CorruptCommittedStoreFailsClosedRatherThanBecomingEmpty(string corruption)
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                await store.SaveAsync(Entry(), CancellationToken.None);
                byte[] bytes = File.ReadAllBytes(store.StorePath);

                switch (corruption)
                {
                    case "magic":
                        bytes[0] = (byte)'X';
                        break;
                    case "version":
                        bytes[8] = 2;
                        bytes[9] = 0;
                        bytes[10] = 0;
                        bytes[11] = 0;
                        break;
                    case "length":
                        bytes[16] = (byte)(bytes[16] ^ 0x01);
                        break;
                    case "checksum":
                        bytes[28] = (byte)(bytes[28] ^ 0x80);
                        break;
                    case "truncated":
                        Array.Resize(ref bytes, bytes.Length - 3);
                        break;
                    case "trailing":
                        Array.Resize(ref bytes, bytes.Length + 1);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown corruption " + corruption);
                }

                File.WriteAllBytes(store.StorePath, bytes);
                var reopened = new FileOutboxStore(directory.Path);
                OutboxStoreCorruptException error = await Assert.ThrowsAsync<OutboxStoreCorruptException>(
                    () => reopened.LoadUnresolvedAsync(CancellationToken.None));
                Assert.Contains("corrupt", error.Message, StringComparison.OrdinalIgnoreCase);

                await Assert.ThrowsAsync<OutboxStoreCorruptException>(
                    () => reopened.SaveAsync(Entry(requestId: "request-2"), CancellationToken.None));
            }
        }

        [Fact]
        public async Task ValidCommittedFileWithUnknownFormatVersionIsRejected()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                await store.SaveAsync(Entry(), CancellationToken.None);
                byte[] bytes = File.ReadAllBytes(store.StorePath);
                bytes[8] = 2;
                bytes[9] = 0;
                bytes[10] = 0;
                bytes[11] = 0;
                File.WriteAllBytes(store.StorePath, bytes);

                OutboxStoreCorruptException error = await Assert.ThrowsAsync<OutboxStoreCorruptException>(
                    () => new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
                Assert.Contains("format version", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task OrphanTempFileDoesNotPoisonACommittedStore()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                await store.SaveAsync(Entry(), CancellationToken.None);
                string temp = store.StorePath + ".tmp-orphan";
                File.WriteAllBytes(temp, new byte[] { 1, 2, 3, 4 });

                Assert.Single(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.False(File.Exists(temp));
            }
        }

        [Fact]
        public async Task OrphanTempFileWithoutACommittedFileIsNotACommittedRecord()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                string temp = store.StorePath + ".tmp-orphan";
                File.WriteAllBytes(temp, new byte[] { 1, 2, 3, 4 });

                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.False(File.Exists(temp));
                Assert.False(File.Exists(store.StorePath));
            }
        }

        [Fact]
        public async Task PreCancelledSaveWritesNothing()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => store.SaveAsync(Entry(), cts.Token));
                }

                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
            }
        }

        [Fact]
        public async Task CancellationAfterTempFlushRemovesTheTempAndLeavesTheOldSnapshot()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry first = Entry(requestId: "request-1");
                await store.SaveAsync(first, CancellationToken.None);

                using (var cts = new CancellationTokenSource())
                {
                    FileOutboxFaultInjection.Observer = (phase, path) =>
                    {
                        if (phase == FileOutboxWritePhase.AfterTempWrite &&
                            path.StartsWith(directory.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            cts.Cancel();
                        }
                    };

                    try
                    {
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => store.SaveAsync(Entry(requestId: "request-2"), cts.Token));
                    }
                    finally
                    {
                        FileOutboxFaultInjection.Observer = null;
                    }
                }

                IReadOnlyList<OutboxEntry> loaded = await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None);
                Assert.Single(loaded);
                Assert.Equal("request-1", loaded[0].RequestId.Value);
                Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
            }
        }

        [Fact]
        public async Task CommitFailureKeepsThePreviousSnapshotAndRemovesTheTemp()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                await store.SaveAsync(Entry(requestId: "request-1"), CancellationToken.None);

                FileOutboxFaultInjection.Observer = (phase, path) =>
                {
                    if (phase == FileOutboxWritePhase.BeforeReplace &&
                        path.StartsWith(directory.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("injected commit failure");
                    }
                };

                try
                {
                    await Assert.ThrowsAsync<IOException>(
                        () => store.SaveAsync(Entry(requestId: "request-2"), CancellationToken.None));
                }
                finally
                {
                    FileOutboxFaultInjection.Observer = null;
                }

                IReadOnlyList<OutboxEntry> loaded = await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None);
                Assert.Single(loaded);
                Assert.Equal("request-1", loaded[0].RequestId.Value);
                Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
            }
        }

        [Fact]
        public async Task RepeatedOpenSeesUpdatesFromAnotherStoreInstance()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var first = new FileOutboxStore(directory.Path);
                var second = new FileOutboxStore(directory.Path);

                await first.SaveAsync(Entry(requestId: "request-1"), CancellationToken.None);
                Assert.Single(await second.LoadUnresolvedAsync(CancellationToken.None));

                await second.SaveAsync(Entry(requestId: "request-2"), CancellationToken.None);
                IReadOnlyList<OutboxEntry> loaded = await first.LoadUnresolvedAsync(CancellationToken.None);
                Assert.Equal(new[] { "request-1", "request-2" }, loaded.Select(e => e.RequestId.Value).ToArray());
            }
        }

        [Fact]
        public async Task ConcurrentSavesAcrossTwoInstancesPreserveEveryEntry()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var first = new FileOutboxStore(directory.Path);
                var second = new FileOutboxStore(directory.Path);
                const int count = 24;
                var tasks = new List<Task>(count);
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    tasks.Add(Task.Run(
                        () => (index % 2 == 0 ? first : second).SaveAsync(
                            Entry(requestId: "request-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            CancellationToken.None),
                        TestContext.Current.CancellationToken));
                }

                await Task.WhenAll(tasks);

                IReadOnlyList<OutboxEntry> loaded = await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None);
                Assert.Equal(count, loaded.Count);
                Assert.Equal(
                    Enumerable.Range(0, count).Select(i => "request-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    loaded.Select(e => e.RequestId.Value).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public async Task EntryBoundFailsExplicitlyWithoutLosingExistingEntries()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path, new FileOutboxOptions { MaxEntries = 1 });
                await store.SaveAsync(Entry(requestId: "request-1"), CancellationToken.None);

                OutboxStoreFullException error = await Assert.ThrowsAsync<OutboxStoreFullException>(
                    () => store.SaveAsync(Entry(requestId: "request-2"), CancellationToken.None));
                Assert.Contains("unresolved-entry bound", error.Message, StringComparison.Ordinal);

                IReadOnlyList<OutboxEntry> loaded = await store.LoadUnresolvedAsync(CancellationToken.None);
                Assert.Single(loaded);
                Assert.Equal("request-1", loaded[0].RequestId.Value);
            }
        }

        [Fact]
        public async Task PayloadBoundFailsBeforeWriting()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path, new FileOutboxOptions { MaxPayloadBytes = 8 });
                await Assert.ThrowsAsync<ArgumentException>(
                    () => store.SaveAsync(Entry(), CancellationToken.None));

                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
                Assert.False(File.Exists(store.StorePath));
            }
        }

        [Fact]
        public async Task ResumeTokenBearingCommandIsRejectedBeforeAnyWrite()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                var writer = new CanonicalPayloadWriter((ushort)CommandKind.ResumeSession);
                writer.WriteString(CommandPayloadFields.ResumeToken, "SECRET-RESUME-TOKEN");
                writer.WriteUInt64(CommandPayloadFields.LastKnownStateVersion, 5);
                BusinessPayload payload = writer.Build();
                var command = new ProtocolCommand(
                    CommandKind.ResumeSession,
                    payload,
                    new RequestId("request-resume"),
                    new RequestScopeId("scope-1"),
                    new StateVersion(5));
                var scope = new RequestScope(
                    new RequestScopeId("scope-1"),
                    "account-1",
                    ProtocolRole.Player,
                    new RoomId("room-1"),
                    1000,
                    2000);
                var entry = new OutboxEntry(command, scope, new SessionId("session-1"), new RoomId("room-1"));

                await Assert.ThrowsAsync<NotSupportedException>(
                    () => store.SaveAsync(entry, CancellationToken.None));
                Assert.False(File.Exists(store.StorePath));
                Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task TransportTicketIsNeverWrittenByTheFileStore()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                var channel = new FakeTableChannel();
                var session = new TableSession(channel, store);

                await session.OpenAsync(
                    new TableSessionOpenRequest("ticket-SENTINEL-DO-NOT-PERSIST", "client-build-1"),
                    CancellationToken.None);
                channel.Enqueue(TestWire.Welcome());
                await session.PumpOnceAsync(CancellationToken.None);
                channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
                await session.PumpOnceAsync(CancellationToken.None);
                await session.SubmitAsync(TestWire.Command(), CancellationToken.None);

                byte[] committed = File.ReadAllBytes(store.StorePath);
                Assert.Equal(-1, IndexOf(committed, Encoding.ASCII.GetBytes("ticket-SENTINEL-DO-NOT-PERSIST")));
                Assert.True(IndexOf(committed, Encoding.ASCII.GetBytes("request-1")) >= 0);
            }
        }

        [Fact]
        public async Task UnknownOutcomeAfterRestartQueriesTheOriginalRequestInsteadOfResending()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var seed = new FileOutboxStore(directory.Path);
                OutboxEntry original = Entry(requestId: "request-restart", scopeId: "scope-1");
                await seed.SaveAsync(original, CancellationToken.None);

                var reopened = new FileOutboxStore(directory.Path);
                var channel = new FakeTableChannel();
                var session = new TableSession(channel, reopened);
                await session.OpenAsync(new TableSessionOpenRequest("ticket-2", "client-build-1"), CancellationToken.None);
                channel.Enqueue(TestWire.Welcome());
                await session.PumpOnceAsync(CancellationToken.None);
                channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
                await session.PumpOnceAsync(CancellationToken.None);

                Assert.Equal(0, channel.CountSent(WireMessageType.Command));
                Assert.Equal(1, channel.CountSent(WireMessageType.ReceiptQuery));
                WireEnvelope query = TestWire.OutboundReceiptQuery(channel, "request-restart");
                Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
                PendingCommandSnapshot pending = Assert.Single(session.PendingCommands);
                Assert.True(pending.DeliveryUnknown);
                Assert.True(pending.NeedsReconciliation);
                Assert.Equal("account-1", pending.Key.AccountId);

                string firstCorrelation = query.CorrelationId;
                channel.Enqueue(TestWire.ReceiptLookup(firstCorrelation, WireLookupStatus.Unknown));
                await session.PumpOnceAsync(CancellationToken.None);

                Assert.Single(session.PendingCommands);
                Assert.Equal(ReceiptLookupStatus.Unknown, session.PendingCommands[0].LastLookup);
                Assert.Equal(0, channel.CountSent(WireMessageType.Command));

                await session.ReconcilePendingAsync(CancellationToken.None);
                Assert.Equal(2, channel.CountSent(WireMessageType.ReceiptQuery));
                string secondCorrelation = channel.LastSent(WireMessageType.ReceiptQuery).CorrelationId;
                WireEnvelope receiptEnvelope = TestWire.AppliedReceipt(11, "scope-1", "request-restart", original.DigestSha256, 5);
                channel.Enqueue(TestWire.ReceiptLookup(secondCorrelation, WireLookupStatus.Found, receiptEnvelope.Receipt, original.DigestSha256));
                await session.PumpOnceAsync(CancellationToken.None);

                Assert.Empty(session.PendingCommands);
                Assert.Equal(0, channel.CountSent(WireMessageType.Command));
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task ReasonAt512AsciiBytesIsAcceptedAndReadable()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution resolution = RejectedResolution(entry, new string('r', 512));

                await store.ResolveAsync(entry.Key, resolution, CancellationToken.None);

                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task ReasonAt513AsciiBytesIsRejectedBeforeCommitAndSnapshotIsUnchanged()
        {
            await AssertResolutionRejected(entry => RejectedResolution(entry, new string('r', 513)));
        }

        [Fact]
        public async Task Utf8TwoByteReasonAt512BytesIsAcceptedAnd514IsRejected()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution accepted = RejectedResolution(entry, new string('\u00e9', 256));
                await store.ResolveAsync(entry.Key, accepted, CancellationToken.None);
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }

            await AssertResolutionRejected(entry => RejectedResolution(entry, new string('\u00e9', 257)));
        }

        [Fact]
        public async Task Utf8ThreeByteReasonAt510BytesIsAcceptedAnd513IsRejected()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution accepted = RejectedResolution(entry, new string('\u4e2d', 170));
                await store.ResolveAsync(entry.Key, accepted, CancellationToken.None);
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }

            await AssertResolutionRejected(entry => RejectedResolution(entry, new string('\u4e2d', 171)));
        }

        [Fact]
        public async Task Utf8FourByteReasonAt512BytesIsAcceptedAnd516IsRejected()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                string acceptedText = string.Concat(Enumerable.Repeat("\U0001F600", 128));
                OutboxResolution accepted = RejectedResolution(entry, acceptedText);
                await store.ResolveAsync(entry.Key, accepted, CancellationToken.None);
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }

            await AssertResolutionRejected(entry => RejectedResolution(entry, string.Concat(Enumerable.Repeat("\U0001F600", 129))));
        }

        [Fact]
        public async Task AppliedWithZeroCommittedAtIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => OutboxResolution.FromReceipt(
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(5),
                new StateVersion(6),
                new EventSeq(1),
                committedAtMs: 0,
                sha256: entry.DigestSha256,
                fromLookup: true));
        }

        [Fact]
        public async Task DispositionAndReceiptStatusMismatchIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Rejected,
                "REJECTED",
                new StateVersion(5),
                null,
                null,
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task AppliedWithoutCommandVersionIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(5),
                null,
                new EventSeq(1),
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task AppliedWithWrongCommandVersionIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(5),
                new StateVersion(7),
                new EventSeq(1),
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task AppliedWithZeroOrMissingEventSeqIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(5),
                new StateVersion(6),
                new EventSeq(0),
                null,
                entry.DigestSha256,
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(5),
                new StateVersion(6),
                null,
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task AppliedAtMaxStateVersionIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                ReceiptStatus.Applied,
                string.Empty,
                new StateVersion(ulong.MaxValue),
                new StateVersion(0),
                new EventSeq(1),
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task RejectedWithoutReasonOrWithCommandVersionIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Rejected,
                ReceiptStatus.Rejected,
                string.Empty,
                new StateVersion(5),
                null,
                null,
                null,
                entry.DigestSha256,
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Rejected,
                ReceiptStatus.Rejected,
                "REJECTED",
                new StateVersion(5),
                new StateVersion(6),
                null,
                null,
                entry.DigestSha256,
                true));
        }

        [Fact]
        public async Task ConflictWithDefinitiveFieldsIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Unknown,
                "REQUEST_ID_CONFLICT",
                new StateVersion(1),
                null,
                null,
                null,
                entry.DigestSha256,
                false));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Unknown,
                "REQUEST_ID_CONFLICT",
                new StateVersion(0),
                new StateVersion(1),
                null,
                null,
                entry.DigestSha256,
                false));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Unknown,
                "REQUEST_ID_CONFLICT",
                new StateVersion(0),
                null,
                null,
                1,
                entry.DigestSha256,
                false));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Unknown,
                "REQUEST_ID_CONFLICT",
                new StateVersion(0),
                null,
                null,
                null,
                entry.DigestSha256,
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Applied,
                "REQUEST_ID_CONFLICT",
                new StateVersion(0),
                null,
                null,
                null,
                entry.DigestSha256,
                false));
        }

        [Fact]
        public async Task UnknownEnumValuesAndInvalidDigestAreRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => new OutboxResolution(
                (OutboxDisposition)99,
                ReceiptStatus.Applied,
                "REJECTED",
                new StateVersion(5),
                new StateVersion(6),
                new EventSeq(1),
                null,
                entry.DigestSha256,
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Applied,
                (ReceiptStatus)99,
                "REJECTED",
                new StateVersion(5),
                new StateVersion(6),
                new EventSeq(1),
                null,
                entry.DigestSha256,
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Rejected,
                ReceiptStatus.Rejected,
                "REJECTED",
                new StateVersion(5),
                null,
                null,
                null,
                new string('a', 63),
                true));

            await AssertResolutionRejected(entry => new OutboxResolution(
                OutboxDisposition.Rejected,
                ReceiptStatus.Rejected,
                "REJECTED",
                new StateVersion(5),
                null,
                null,
                null,
                new string('z', 64),
                true));
        }

        [Fact]
        public async Task RejectedWithPositiveCommittedAtAndConflictTombstoneAreAccepted()
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution rejected = OutboxResolution.FromReceipt(
                    ReceiptStatus.Rejected,
                    "STALE_DECISION",
                    new StateVersion(5),
                    null,
                    null,
                    committedAtMs: 1234,
                    sha256: entry.DigestSha256,
                    fromLookup: true);
                await store.ResolveAsync(entry.Key, rejected, CancellationToken.None);
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }

            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                OutboxResolution conflict = OutboxResolution.Conflict("REQUEST_ID_CONFLICT", entry.DigestSha256);
                await store.ResolveAsync(entry.Key, conflict, CancellationToken.None);
                Assert.Empty(await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task UnpairedSurrogateReasonIsRejectedBeforeCommit()
        {
            await AssertResolutionRejected(entry => RejectedResolution(entry, "\ud800"));
        }

        private static async Task AssertResolutionRejected(Func<OutboxEntry, OutboxResolution> factory)
        {
            using (var directory = new TempOutboxDirectory())
            {
                var store = new FileOutboxStore(directory.Path);
                OutboxEntry entry = Entry();
                await store.SaveAsync(entry, CancellationToken.None);
                byte[] before = File.ReadAllBytes(store.StorePath);

                await Assert.ThrowsAsync<ArgumentException>(
                    () => store.ResolveAsync(entry.Key, factory(entry), CancellationToken.None));

                Assert.Equal(before, File.ReadAllBytes(store.StorePath));
                IReadOnlyList<OutboxEntry> loaded = await new FileOutboxStore(directory.Path).LoadUnresolvedAsync(CancellationToken.None);
                Assert.Single(loaded);
                Assert.Equal(entry.Key, loaded[0].Key);
                Assert.Equal(entry.CopyCanonicalPayload(), loaded[0].CopyCanonicalPayload());
            }
        }

        private static OutboxResolution RejectedResolution(OutboxEntry entry, string reason)
        {
            return OutboxResolution.FromReceipt(
                ReceiptStatus.Rejected,
                reason,
                new StateVersion(5),
                null,
                null,
                null,
                entry.DigestSha256,
                fromLookup: true);
        }

        private static OutboxResolution AppliedResolution(OutboxEntry entry, bool fromLookup)
        {
            return OutboxResolution.FromReceipt(
                ReceiptStatus.Applied,
                null,
                new StateVersion(entry.ExpectedStateVersion.Value),
                new StateVersion(entry.ExpectedStateVersion.Value + 1),
                new EventSeq(11),
                1234,
                entry.DigestSha256,
                fromLookup);
        }

        internal static OutboxEntry Entry(
            string requestId = "request-1",
            string accountId = "account-1",
            string scopeId = "scope-1",
            string sessionId = "session-1",
            string roomId = "room-1",
            ulong expectedStateVersion = 5,
            string roundId = "round-1",
            string decisionId = "decision-1",
            ulong controlEpoch = 7,
            ulong ownerEpoch = 3,
            ulong issuedAtMs = 1000,
            ulong expiresAtMs = 2000,
            uint[]? tileIds = null)
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                roundId,
                decisionId,
                controlEpoch,
                ProtocolActionType.DiscardTile,
                tileIds: tileIds ?? new uint[] { 5, 6 });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId(scopeId),
                new StateVersion(expectedStateVersion),
                new RoundId(roundId),
                new DecisionId(decisionId),
                new ControlEpoch(controlEpoch),
                new OwnerEpoch(ownerEpoch));
            var scope = new RequestScope(
                new RequestScopeId(scopeId),
                accountId,
                ProtocolRole.Player,
                new RoomId(roomId),
                issuedAtMs,
                expiresAtMs);
            return new OutboxEntry(command, scope, new SessionId(sessionId), new RoomId(roomId));
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0)
            {
                return 0;
            }

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Real child-process tests. These prove crash/restart behavior of the
    /// on-disk snapshot on the host file system; they do not simulate power
    /// loss, a disk cache flush failure, Android/iOS fsync, or a platform
    /// secure-storage boundary.
    /// </summary>
    public sealed class FileOutboxCrashWorkerTests
    {
        [Fact]
        public async Task KilledBeforeSaveLeavesNoCommittedRecord()
        {
            using (var directory = new TempOutboxDirectory())
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
            using (var directory = new TempOutboxDirectory())
            {
                var seed = new FileOutboxStore(directory.Path);
                OutboxEntry first = FileOutboxStoreTests.Entry("request-1");
                await seed.SaveAsync(first, CancellationToken.None);
                byte[] committedBefore = File.ReadAllBytes(seed.StorePath);

                var worker = StartWorker("save-block", directory.Path, "request-2", "AfterTempFlush");
                await using (worker)
                {
                    Assert.Equal("READY", await worker.ReadLineAsync(TimeSpan.FromSeconds(20)));
                    string phase = await worker.ReadLineAsync(TimeSpan.FromSeconds(20));
                    Assert.StartsWith("PHASE AfterTempFlush", phase, StringComparison.Ordinal);
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
            using (var directory = new TempOutboxDirectory())
            {
                var worker = StartWorker("save-hold", directory.Path, "request-1");
                await using (worker)
                {
                    string saved = await worker.ReadLineAsync(TimeSpan.FromSeconds(20));
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
        public async Task ConcurrentWorkerProcessesSerializeCommitsAndPreserveEveryEntry()
        {
            using (var directory = new TempOutboxDirectory())
            {
                const int workers = 6;
                var processes = new List<CrashWorkerProcess>(workers);
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
        public async Task KilledBeforeResolveKeepsTheUnknownEntryForReceiptQuery()
        {
            using (var directory = new TempOutboxDirectory())
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
        public async Task UnknownOutcomeAfterARealWorkerExitIsQueriedNotResent()
        {
            using (var directory = new TempOutboxDirectory())
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

                Assert.Equal(0, channel.CountSent(WireMessageType.Command));
                Assert.Equal(1, channel.CountSent(WireMessageType.ReceiptQuery));
                WireEnvelope query = TestWire.OutboundReceiptQuery(channel, "request-1");
                Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
                PendingCommandSnapshot pending = Assert.Single(session.PendingCommands);
                Assert.True(pending.DeliveryUnknown);
                Assert.True(pending.NeedsReconciliation);
            }
        }

        [Fact]
        public async Task KilledAfterResolveDoesNotResurrectTheRequest()
        {
            using (var directory = new TempOutboxDirectory())
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

        private static CrashWorkerProcess StartWorker(params string[] args)
        {
            string workerPath = Path.Combine(AppContext.BaseDirectory, "outbox-worker", "Mahjong.Outbox.CrashWorker.dll");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException(
                    "The test-only crash worker was not copied next to the test assembly. Build the test project first.",
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

            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the crash worker process.");
            return new CrashWorkerProcess(process);
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
    }

    internal sealed class CrashWorkerProcess : IAsyncDisposable
    {
        private readonly Process _process;

        public CrashWorkerProcess(Process process)
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
                    "Timed out waiting for the crash worker to emit a line. Exited=" + _process.HasExited + ".");
            }

            string? line = await read;
            if (line == null)
            {
                throw new InvalidOperationException(
                    "The crash worker exited before emitting the expected line. stderr: " +
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
                throw new TimeoutException("Timed out waiting for the crash worker to exit.");
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

    internal sealed class TempOutboxDirectory : IDisposable
    {
        public TempOutboxDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mahjong-file-outbox-tests",
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
