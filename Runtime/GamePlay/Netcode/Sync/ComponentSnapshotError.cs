//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>Local validation result, not a wire enum. None accompanies success only on normal return.</summary>
    public enum ComponentSnapshotError
    {
        None = 0,
        InvalidLimits,
        InvalidContext,
        InvalidCodec,
        InvalidHeader,
        HeaderTruncated,
        InvalidMagic,
        UnsupportedVersion,
        InvalidHeaderSize,
        SessionEpochMismatch,
        WorldEpochMismatch,
        ComponentSchemaMismatch,
        CodecRevisionMismatch,
        StateBitCountMismatch,
        CodecIdentityMismatch,
        EntityCountMismatch,
        TooManyRecords,
        StateTooLarge,
        FrameTooLarge,
        FrameLengthMismatch,
        DestinationTooSmall,
        ScratchTooSmall,
        InvalidEntityKey,
        EntityOrderViolation,
        NonCanonicalPadding,
        StateRejected,
        CodecBitCountMismatch
    }
}
