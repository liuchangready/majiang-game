using Xunit;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// Heavy real-channel suites share one serial collection so authority clock/session timing
    /// tests cannot starve each other under the default parallel test scheduler. This is test
    /// scheduling only; production concurrency is unchanged.
    /// </summary>
    [CollectionDefinition("LocalHostSerial", DisableParallelization = true)]
    public sealed class LocalHostSerialCollection
    {
    }
}
