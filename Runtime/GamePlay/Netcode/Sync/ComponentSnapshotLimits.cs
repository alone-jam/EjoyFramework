//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable
using System;

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Immutable independent admission budgets, not buffer allocations. The constructor rejects
    /// invalid configuration; default(struct) is deliberately invalid and codecs fail closed.
    /// Legal maxima may have a product above MaxFrameBytes: that frame is rejected using wide
    /// arithmetic before offsets, record loops or typed codec calls. Zero records/bits are valid.
    /// </summary>
    public readonly struct ComponentSnapshotLimits
    {
        public const int MinimumFrameBytes = 48;
        public const int MaximumRecords = 16 * 1024;
        public const int MaximumStateBits = 64 * 1024;
        public const int MaximumFrameBytes = 8 * 1024 * 1024;

        public static ComponentSnapshotLimits Default => new ComponentSnapshotLimits(256, 2048, 128 * 1024);
        public int MaxRecords { get; }
        public int MaxStateBits { get; }
        public int MaxFrameBytes { get; }
        public bool IsValid => MaxRecords >= 0 && MaxRecords <= MaximumRecords
            && MaxStateBits >= 0 && MaxStateBits <= MaximumStateBits
            && MaxFrameBytes >= MinimumFrameBytes && MaxFrameBytes <= MaximumFrameBytes;

        public ComponentSnapshotLimits(int maxRecords, int maxStateBits, int maxFrameBytes)
        {
            if (maxRecords < 0 || maxRecords > MaximumRecords)
                throw new ArgumentOutOfRangeException(nameof(maxRecords));
            if (maxStateBits < 0 || maxStateBits > MaximumStateBits)
                throw new ArgumentOutOfRangeException(nameof(maxStateBits));
            if (maxFrameBytes < MinimumFrameBytes || maxFrameBytes > MaximumFrameBytes)
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            MaxRecords = maxRecords;
            MaxStateBits = maxStateBits;
            MaxFrameBytes = maxFrameBytes;
        }
    }
}
