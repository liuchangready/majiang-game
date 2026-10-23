using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class WssEndpointTests
    {
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

        [Fact(Skip = "NOT_RUN: no trusted local test root is installed; trusting a test CA would modify the machine store. Production TLS validation is not disabled.")]
        public void PositiveLocalWssTrustChainIsNotRunWithoutATrustedTestRoot()
        {
            // Deliberately skipped rather than passing by disabling
            // certificate validation. The negative test above proves that
            // default validation is still active.
        }
    }
}
