//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// v1 homogeneous full-component batch metadata, validated by the codec rather than this
    /// value constructor. Session/world/snapshot/server tick/schema/revision must be nonzero.
    /// LastProcessedCommandTick may be zero, but cannot exceed ServerTick; ticks never wrap
    /// within the timeline. SnapshotId is per-recipient identity, NOT a tick or packet ack.
    /// </summary>
    /// <remarks>
    /// Included values are standalone, not a complete world and not an omission/despawn list.
    /// Epoch and ack values are claims, not authentication or proof of authoritative commit.
    /// </remarks>
    public readonly struct ComponentSnapshotHeader
    {
        public ulong SessionEpoch { get; }
        public uint WorldEpoch { get; }
        public ulong SnapshotId { get; }
        public uint ServerTick { get; }
        public uint LastProcessedCommandTick { get; }
        public ushort ComponentSchemaId { get; }
        public ushort CodecRevision { get; }
        public uint StateBitCount { get; }
        public uint EntityCount { get; }

        public ComponentSnapshotHeader(ulong sessionEpoch, uint worldEpoch, ulong snapshotId,
            uint serverTick, uint lastProcessedCommandTick, ushort componentSchemaId,
            ushort codecRevision, uint stateBitCount, uint entityCount)
        {
            SessionEpoch = sessionEpoch;
            WorldEpoch = worldEpoch;
            SnapshotId = snapshotId;
            ServerTick = serverTick;
            LastProcessedCommandTick = lastProcessedCommandTick;
            ComponentSchemaId = componentSchemaId;
            CodecRevision = codecRevision;
            StateBitCount = stateBitCount;
            EntityCount = entityCount;
        }
    }
}
