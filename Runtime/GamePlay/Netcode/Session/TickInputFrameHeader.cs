//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Values of a v1 tick-input header. Construction does not validate: the codec validates
    /// every field before writing or publishing. CommandSchemaId describes INPUT COMMANDS,
    /// not ECS component schemas. CommandTick never wraps within a timeline; zero is invalid.
    /// </summary>
    public readonly struct TickInputFrameHeader
    {
        public ulong SessionEpoch { get; }
        public uint WorldEpoch { get; }
        public uint CommandTick { get; }
        public ushort CommandSchemaId { get; }
        public ushort CodecRevision { get; }
        public uint PayloadBitLength { get; }

        public TickInputFrameHeader(ulong sessionEpoch, uint worldEpoch, uint commandTick,
            ushort commandSchemaId, ushort codecRevision, uint payloadBitLength)
        {
            SessionEpoch = sessionEpoch;
            WorldEpoch = worldEpoch;
            CommandTick = commandTick;
            CommandSchemaId = commandSchemaId;
            CodecRevision = codecRevision;
            PayloadBitLength = payloadBitLength;
        }
    }
}
