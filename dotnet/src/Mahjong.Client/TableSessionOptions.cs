using System;

namespace Mahjong.Client
{
    /// <summary>
    /// Client-local limits. The server's WELCOME limits may lower the early
    /// buffer further; they can never raise the hard client cap.
    /// </summary>
    public sealed class TableSessionOptions
    {
        public const int DefaultMaxEarlyBufferItems = 64;
        public const long DefaultMaxEarlyBufferBytes = 4L * 1024L * 1024L;

        public int MaxEarlyBufferItems { get; set; } = DefaultMaxEarlyBufferItems;

        public long MaxEarlyBufferBytes { get; set; } = DefaultMaxEarlyBufferBytes;

        internal void Validate()
        {
            if (MaxEarlyBufferItems <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxEarlyBufferItems), MaxEarlyBufferItems, "Early buffer item limit must be positive.");
            }

            if (MaxEarlyBufferBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxEarlyBufferBytes), MaxEarlyBufferBytes, "Early buffer byte limit must be positive.");
            }
        }
    }
}
