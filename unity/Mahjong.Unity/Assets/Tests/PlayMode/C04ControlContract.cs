using System;

namespace Mahjong.Unity.Tests.PlayMode
{
    public static class C04ControlContract
    {
        public static void Assert(
            bool controlPresent,
            bool epochAdvanced,
            bool human,
            string expectedStaleReason,
            string actualStaleReason,
            bool staleRejected,
            int staleCommandFrames,
            int stalePendingAfter)
        {
            if (!controlPresent) throw new InvalidOperationException("control projection is null");
            if (!epochAdvanced) throw new InvalidOperationException("control epoch did not advance");
            if (!human) throw new InvalidOperationException("control is still automated");
            if (!string.Equals(expectedStaleReason, actualStaleReason, StringComparison.Ordinal))
                throw new InvalidOperationException($"stale reason {actualStaleReason} != expected {expectedStaleReason}");
            if (!staleRejected) throw new InvalidOperationException("stale request was not rejected");
            if (staleCommandFrames != 1) throw new InvalidOperationException($"stale request command frames {staleCommandFrames} != 1");
            if (stalePendingAfter != 0) throw new InvalidOperationException("stale request left pending target command");
        }
    }
}
