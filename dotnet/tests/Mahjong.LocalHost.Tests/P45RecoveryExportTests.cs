using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.LocalHost;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class P45RecoveryExportTests
    {
        [Fact]
        public async Task CaptureUsesOnePersistedVersionAndSurvivesColdReopen()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            LocalTableRecoveryCapture first = await OpenAndCaptureAsync(harness, cts.Token);
            Assert.Equal("0", first.Facts.StateVersion);
            Assert.Equal("0", first.Facts.EventSeq);
            Assert.Equal(P41Harness.RoomId + "-r1", first.Facts.RoundId);
            Assert.Equal(136, first.Facts.Physical.TileCount);
            Assert.Equal(136, first.Facts.Physical.UniqueCount);
            Assert.Empty(first.Facts.Requests);
            Assert.Equal(0, first.Facts.Ledger.EntryCount);
            Assert.Equal("LATEST_PERSISTED_CHECKPOINT", first.Facts.Ledger.Source);
            Assert.Equal(first.AuthoritySaveBytes.Length, first.Facts.AuthoritySaveBytes);
            Assert.Equal(Sha256Hex(first.AuthoritySaveBytes), first.Facts.AuthoritySaveSha256);

            LocalTableRecoveryCapture second = await OpenAndCaptureAsync(harness, cts.Token);
            Assert.Equal(first.Facts.RoundId, second.Facts.RoundId);
            Assert.Equal(first.Facts.StateVersion, second.Facts.StateVersion);
            Assert.Equal(first.Facts.EventSeq, second.Facts.EventSeq);
            Assert.Equal(first.Facts.StateFingerprint, second.Facts.StateFingerprint);
            Assert.Equal(first.Facts.StateHash, second.Facts.StateHash);
            Assert.Equal(first.Facts.Physical.TilesSha256, second.Facts.Physical.TilesSha256);
            Assert.Equal(first.Facts.Ledger.EntriesSha256, second.Facts.Ledger.EntriesSha256);
            Assert.Equal(first.Facts.Ledger.BalancesSha256, second.Facts.Ledger.BalancesSha256);
            Assert.Equal(first.Facts.ControlVersion, second.Facts.ControlVersion);
            Assert.Equal(first.Facts.ControlEpochs, second.Facts.ControlEpochs);
        }

        [Fact]
        public async Task PreparedCaptureRunsBeforeDriverAndIsTakenOnce()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LocalTableHost.PrepareRecoveryCaptureOnNextOpen(harness.Options.SaveDirectory);
            Assert.True(LocalTableDiagnosticRegistry.HasPendingCapture(harness.Options.SaveDirectory), "pending capture was not registered");
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cts.Token);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "p45-prepared-test", null), cts.Token);
            try
            {
                LocalTableRecoveryCapture capture = await LocalTableHost.TakePreparedRecoveryCaptureAsync(
                    harness.Options.SaveDirectory,
                    cts.Token);
                Assert.Equal("0", capture.Facts.StateVersion);
                Assert.Equal(136, capture.Facts.Physical.TileCount);
                await Assert.ThrowsAsync<LocalTableException>(async () =>
                    await LocalTableHost.TakePreparedRecoveryCaptureAsync(harness.Options.SaveDirectory, cts.Token));
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_PREPARED_TEST_CLOSE", true),
                    CancellationToken.None);
            }
        }

        [Fact]
        public async Task TamperedAuthorityEnvelopeIsRejected()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LocalTableRecoveryCapture capture = await OpenAndCaptureAsync(harness, cts.Token);
            byte[] tampered = (byte[])capture.AuthoritySaveBytes.Clone();
            tampered[tampered.Length - 1] ^= 0x01;
            Assert.Throws<InvalidDataException>(() => LocalTableRecoveryFactsReader.ReadPersistedFacts(tampered));
        }

        private static async Task<LocalTableRecoveryCapture> OpenAndCaptureAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(
                new TableChannelConnectRequest(ticket, "p45-recovery-test", null),
                cancellationToken);
            try
            {
                return await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, cancellationToken);
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_RECOVERY_TEST_CLOSE", true),
                    CancellationToken.None);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToUpperInvariant();
        }
    }
}
