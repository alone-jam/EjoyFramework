//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Exact expected admission context; no wildcard, fallback or version downgrade. Epochs,
    /// schema and revision must be nonzero. A zero bit width means exactly a zero-bit tag.
    /// Construction copies values; TryDecode validates these and the selected codec identity.
    /// </summary>
    public readonly struct ComponentSnapshotContext
    {
        public ulong SessionEpoch { get; }
        public uint WorldEpoch { get; }
        public ushort ComponentSchemaId { get; }
        public ushort CodecRevision { get; }
        public uint StateBitCount { get; }

        public ComponentSnapshotContext(ulong sessionEpoch, uint worldEpoch, ushort componentSchemaId,
            ushort codecRevision, uint stateBitCount)
        {
            SessionEpoch = sessionEpoch;
            WorldEpoch = worldEpoch;
            ComponentSchemaId = componentSchemaId;
            CodecRevision = codecRevision;
            StateBitCount = stateBitCount;
        }
    }
}
