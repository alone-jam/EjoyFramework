//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable
using EjoyFramework.GamePlay.Netcode.Messages;

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Explicit field-wise full-state layout. Schema/revision are nonzero, and StateBitCount is
    /// fixed for that revision (zero is a valid tag). Implementations must be deterministic,
    /// stateless value codecs: no raw-memory serialization, reflection, pointers, retained
    /// buffers, external mutable state, per-record allocations or changes to the supplied cursor's
    /// backing storage/limit. Use the bounded reader/writer, consuming exactly StateBitCount.
    /// </summary>
    /// <remarks>
    /// Validate ranges, finite numbers and allowed flag bits in BOTH directions. Return false for
    /// invalid values; on read, return a newly constructed owned value, never a borrowed address.
    /// Unmanaged alone does not establish these semantic requirements. Unexpected exceptions are
    /// programmer errors and propagate through the frame codec with success outputs unpublished.
    /// This identity does not register TState: the application later cross-checks its Core schema
    /// catalog. This layer deliberately has no Core.Ecs dependency or second schema catalog.
    /// </remarks>
    public interface IComponentStateCodec<TState> where TState : unmanaged
    {
        ushort ComponentSchemaId { get; }
        ushort CodecRevision { get; }
        uint StateBitCount { get; }
        bool TryWrite(ref NetBitWriter writer, in TState state);
        bool TryRead(ref NetBitReader reader, out TState state);
    }
}
