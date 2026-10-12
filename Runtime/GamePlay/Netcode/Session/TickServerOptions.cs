using System;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Immutable server work budgets. Byte budgets must permit one maximum configured frame.</summary>
    public sealed class TickServerOptions
    {
        public int MaxPeers { get; }
        public int CommandWindow { get; }
        public int MaxPayloadBits { get; }
        public int MaxPollEvents { get; }
        public int MaxPollDataBytes { get; }
        public int MaxPeerPacketsPerFrame { get; }
        public int MaxPeerDataBytesPerFrame { get; }
        public int TickRate { get; }
        public int MaxTicksPerFrame { get; }
        public double MaxFrameDelta { get; }
        public uint InitialTick { get; }
        public int MaximumFrameBytes { get; }

        public TickServerOptions(int maxPeers = 32, int commandWindow = 32,
            int maxPayloadBits = TickInputFrameCodec.DefaultMaxPayloadBits, int maxPollEvents = 512,
            int maxPollDataBytes = 1024 * 1024, int maxPeerPacketsPerFrame = 64,
            int maxPeerDataBytesPerFrame = 64 * 1024, int tickRate = 30, int maxTicksPerFrame = 4,
            double maxFrameDelta = 0.25, uint initialTick = 0)
        {
            Range(maxPeers, 1, 1024, nameof(maxPeers));
            Range(commandWindow, 1, 1024, nameof(commandWindow));
            Range(maxPayloadBits, 0, TickInputFrameCodec.MaximumPayloadBits, nameof(maxPayloadBits));
            Range(maxPollEvents, 1, 65536, nameof(maxPollEvents));
            Range(maxPeerPacketsPerFrame, 1, maxPollEvents, nameof(maxPeerPacketsPerFrame));
            int frameBytes = checked(TickInputFrameCodec.HeaderSize + (int)(((long)maxPayloadBits + 7) / 8));
            Range(maxPollDataBytes, frameBytes, 16 * 1024 * 1024, nameof(maxPollDataBytes));
            Range(maxPeerDataBytesPerFrame, frameBytes, maxPollDataBytes, nameof(maxPeerDataBytesPerFrame));
            Range(tickRate, 1, 1000, nameof(tickRate));
            Range(maxTicksPerFrame, 1, 128, nameof(maxTicksPerFrame));
            if (!Finite(maxFrameDelta) || maxFrameDelta <= 0 || !Finite(maxFrameDelta * tickRate))
                throw new ArgumentOutOfRangeException(nameof(maxFrameDelta));
            MaxPeers = maxPeers; CommandWindow = commandWindow; MaxPayloadBits = maxPayloadBits;
            MaxPollEvents = maxPollEvents; MaxPollDataBytes = maxPollDataBytes;
            MaxPeerPacketsPerFrame = maxPeerPacketsPerFrame; MaxPeerDataBytesPerFrame = maxPeerDataBytesPerFrame;
            TickRate = tickRate; MaxTicksPerFrame = maxTicksPerFrame; MaxFrameDelta = maxFrameDelta;
            InitialTick = initialTick; MaximumFrameBytes = frameBytes;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void Range(int value, int minimum, int maximum, string name)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
        }
    }
}
