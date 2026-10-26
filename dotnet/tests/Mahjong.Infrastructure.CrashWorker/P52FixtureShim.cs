using System;
using System.Collections.Generic;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.Infrastructure.Tests
{
    /// <summary>Minimal test-only shim used by the shared terminal/ledger fixture in the worker.</summary>
    internal static class P52Fixture
    {
        internal static TableControlCheckpoint Control(long controlVersion = 0L)
        {
            var registry = new ControlRegistry(
                new DisconnectPolicy(),
                new Dictionary<int, SeatControllerKind>
                {
                    [0] = SeatControllerKind.RemoteHuman,
                    [1] = SeatControllerKind.RemoteHuman,
                    [2] = SeatControllerKind.RemoteHuman,
                    [3] = SeatControllerKind.RemoteHuman
                });
            return new TableControlCheckpoint(registry.Capture(), controlVersion);
        }
    }
}
