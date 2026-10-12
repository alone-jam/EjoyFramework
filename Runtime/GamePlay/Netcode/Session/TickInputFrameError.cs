//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Offline framing errors; no transport/session state is changed or admitted by the codec.</summary>
    public enum TickInputFrameError : byte
    {
        None,
        InvalidPayloadLimit,
        InvalidContext,
        InvalidHeader,
        HeaderTruncated,
        InvalidMagic,
        UnsupportedVersion,
        InvalidHeaderSize,
        SessionEpochMismatch,
        WorldEpochMismatch,
        CommandSchemaMismatch,
        CodecRevisionMismatch,
        PayloadTooLarge,
        PayloadLengthMismatch,
        NonCanonicalPadding,
        DestinationTooSmall
    }
}
