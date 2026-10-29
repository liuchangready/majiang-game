using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Unity.Platform;

namespace Mahjong.Unity.App
{
    public sealed class LocalTableUnavailableException : Exception
    {
        public const string ErrorCode = "LOCAL_HOST_UNAVAILABLE";
        public LocalTableUnavailableException(string message) : base(message) { }
    }

    public sealed class UnavailableLocalTableChannelFactory : ITableChannelFactory
    {
        public Task<ITableChannel> CreateAsync(
            TableTransportKind kind,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<ITableChannel>(cancellationToken);
            }

            if (kind != TableTransportKind.Local)
            {
                return Task.FromException<ITableChannel>(
                    new NotSupportedException("WSS transport is not part of P4.2-core."));
            }

            return Task.FromException<ITableChannel>(
                new LocalTableUnavailableException("Local host is not available in P4.2-core."));
        }
    }
}
