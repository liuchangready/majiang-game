using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class WssEndpointTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void PlainWsRequiresExplicitLoopbackTestOptIn()
        {
            var options = new WebSocketTableChannelOptions(new Uri("ws://127.0.0.1:12345/"));

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            {
                using var channel = new WebSocketTableChannel(options);
            });

            Assert.Contains("AllowInsecureLocalWebSocket", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PlainWsIsRejectedForRemoteHostsEvenWithTestOptIn()
        {
            var options = new WebSocketTableChannelOptions(new Uri("ws://example.com/table"))
            {
                AllowInsecureLocalWebSocket = true
            };

            Assert.Throws<ArgumentException>(() =>
            {
                using var channel = new WebSocketTableChannel(options);
            });
        }

        [Fact]
        public void CredentialsOrTicketQueryInUrlAreRejected()
        {
            var userInfo = new WebSocketTableChannelOptions(new Uri("wss://user:secret@localhost/table"));
            var query = new WebSocketTableChannelOptions(new Uri("wss://localhost/table?ticket=secret"));

            Assert.Throws<ArgumentException>(() =>
            {
                using var channel = new WebSocketTableChannel(userInfo);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                using var channel = new WebSocketTableChannel(query);
            });
        }

        [Fact]
        public void WssEndpointIsAcceptedAndPlainWsLoopbackIsAcceptedOnlyWithOptIn()
        {
            using (var secure = new WebSocketTableChannel(new WebSocketTableChannelOptions(new Uri("wss://localhost/table"))))
            {
                Assert.False(secure.IsOpen);
            }

            using (var local = new WebSocketTableChannel(new WebSocketTableChannelOptions(new Uri("ws://127.0.0.1:12345/table"))
            {
                AllowInsecureLocalWebSocket = true
            }))
            {
                Assert.False(local.IsOpen);
            }
        }

        [Fact]
        public async Task SelfSignedWssCertificateIsRejectedByDefaultValidation()
        {
            await using var server = new LocalWebSocketServer(tls: true);
            server.Handler = (index, connection) => Task.CompletedTask;
            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var channel = new WebSocketTableChannel(options);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(
                    new TableChannelConnectRequest("ticket-1", "test-build", null),
                    CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.TlsHandshakeFailed, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task PositiveLocalWssTrustChainCompletesTlsHandshakeAndWireRoundTrip()
        {
            using var authority = TestCertificateAuthority.CreateServerAuthority("mahjong-v5-test.local", IPAddress.Loopback);
            await using var server = new LocalWebSocketServer(authority.ServerLeaf);
            byte[] response = TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11));
            var serverSawPing = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Handler = async (index, connection) =>
            {
                Envelope received = await connection.ReadEnvelopeAsync(CancellationToken.None);
                serverSawPing.TrySetResult(received);
                await connection.SendBinaryAsync(response, CancellationToken.None);
                await HoldConnectionAsync(connection);
            };

            // The channel under test is the production WebSocketTableChannel; only
            // this test instance's socket factory carries the per-test root. The
            // machine store is untouched and production channels keep default OS
            // validation (proven by SelfSignedWssCertificateIsRejectedByDefaultValidation).
            var factory = new TrustedTestRootConnectionFactory(authority.Root);
            var options = new WebSocketTableChannelOptions(new Uri("wss://127.0.0.1:" + server.Port + "/"))
            {
                ReceiveMessageTimeout = TestTimeout
            };
            using var channel = new WebSocketTableChannel(options, factory);
            await channel.OpenAsync(
                new TableChannelConnectRequest("ticket-1", "test-build", null),
                CancellationToken.None);

            Assert.True(channel.IsOpen);
            Assert.Equal(1, server.AcceptedConnections);

            byte[] ping = TestWire.Encode(TestWire.HeartbeatPing(11, "0123456789abcdef"));
            await channel.SendAsync(ping, CancellationToken.None);

            using (var cts = new CancellationTokenSource(TestTimeout))
            {
                ReadOnlyMemory<byte> received = await channel.ReceiveAsync(cts.Token);
                Envelope envelope = TestWire.Decode(received);
                Assert.Equal(MessageType.PlayerUpdate, envelope.MessageType);
                Assert.Equal(6UL, envelope.PlayerUpdate.Cursor.StreamVersion);
            }

            Envelope serverSide = await serverSawPing.Task.WaitAsync(TestTimeout);
            Assert.Equal(MessageType.Heartbeat, serverSide.MessageType);
            Assert.Equal("0123456789abcdef", serverSide.Heartbeat.Ping.Nonce);

            Assert.Equal(1, factory.AcceptedCertificates);
            Assert.Equal(0, factory.RejectedCertificates);
            Assert.Equal(authority.ServerLeaf.Thumbprint, factory.LastAcceptedThumbprint);
            Assert.Null(factory.LastRejectionReason);
            Assert.Empty(server.Errors);
        }

        [Fact]
        public async Task WssLeafForADifferentHostNameIsRejectedByTheStrictTestChain()
        {
            using var authority = TestCertificateAuthority.CreateServerAuthority("mahjong-other-host.example.test", null);
            await using var server = new LocalWebSocketServer(authority.ServerLeaf);
            var factory = new TrustedTestRootConnectionFactory(authority.Root);
            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var channel = new WebSocketTableChannel(options, factory);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(
                    new TableChannelConnectRequest("ticket-1", "test-build", null),
                    CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.TlsHandshakeFailed, ex.Code);
            Assert.True(ex.Permanent);
            Assert.Equal(0, factory.AcceptedCertificates);
            Assert.Equal(1, factory.RejectedCertificates);
            Assert.Contains("hostname", factory.LastRejectionReason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task WssLeafIssuedByAnUntrustedRootIsRejectedByTheStrictTestChain()
        {
            using var trusted = TestCertificateAuthority.CreateServerAuthority("mahjong-v5-test.local", IPAddress.Loopback);
            using var untrusted = TestCertificateAuthority.CreateServerAuthority("mahjong-v5-test.local", IPAddress.Loopback);
            await using var server = new LocalWebSocketServer(untrusted.ServerLeaf);
            var factory = new TrustedTestRootConnectionFactory(trusted.Root);
            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var channel = new WebSocketTableChannel(options, factory);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(
                    new TableChannelConnectRequest("ticket-1", "test-build", null),
                    CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.TlsHandshakeFailed, ex.Code);
            Assert.True(ex.Permanent);
            Assert.Equal(0, factory.AcceptedCertificates);
            Assert.Equal(1, factory.RejectedCertificates);
            Assert.Contains("chain", factory.LastRejectionReason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ExpiredWssLeafIsRejectedByTheStrictTestChain()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using var authority = TestCertificateAuthority.CreateServerAuthority(
                "mahjong-v5-test.local",
                IPAddress.Loopback,
                now.AddDays(-3),
                now.AddDays(-1));
            await using var server = new LocalWebSocketServer(authority.ServerLeaf);
            var factory = new TrustedTestRootConnectionFactory(authority.Root);
            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var channel = new WebSocketTableChannel(options, factory);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(
                    new TableChannelConnectRequest("ticket-1", "test-build", null),
                    CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.TlsHandshakeFailed, ex.Code);
            Assert.True(ex.Permanent);
            Assert.Equal(0, factory.AcceptedCertificates);
            Assert.Equal(1, factory.RejectedCertificates);
            Assert.Contains("validity", factory.LastRejectionReason, StringComparison.Ordinal);
        }

        private static async Task HoldConnectionAsync(WebSocketServerConnection connection)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await connection.ReadMessageAsync(cts.Token);
            }
            catch (Exception)
            {
                // The client closed or the hold timed out.
            }
        }
    }
}
