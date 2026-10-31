using System;

namespace Mahjong.Unity.App
{
    [Serializable]
    public sealed class AppConfig
    {
        public int targetFrameRate = 60;
        public int pumpBarrierTimeoutMs = 2000;
        public int closeTimeoutMs = 3000;
        public int cancelObservationMs = 100;
        public int maxDispatchPerFrame = 128;
        public float maxDispatchMsPerFrame = 4.0f;
    }
}
