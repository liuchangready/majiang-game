using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;

namespace Mahjong.Unity.Platform
{
    public enum TableTransportKind
    {
        Local = 0,
        Wss = 1
    }

    public interface ITableChannelFactory
    {
        Task<ITableChannel> CreateAsync(
            TableTransportKind kind,
            CancellationToken cancellationToken);
    }
}
