//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Exact expected admission context for offline decoding. Every field must be nonzero;
    /// zero never means wildcard. Matching this context is NOT authentication or authorization.
    /// </summary>
    public readonly struct TickInputFrameContext
    {
        public ulong SessionEpoch { get; }
        public uint WorldEpoch { get; }
        public ushort CommandSchemaId { get; }
        public ushort CodecRevision { get; }

        public TickInputFrameContext(ulong sessionEpoch, uint worldEpoch, ushort commandSchemaId, ushort codecRevision)
        {
            SessionEpoch = sessionEpoch;
            WorldEpoch = worldEpoch;
            CommandSchemaId = commandSchemaId;
            CodecRevision = codecRevision;
        }
    }
}
