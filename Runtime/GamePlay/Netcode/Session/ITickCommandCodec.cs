//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using EjoyFramework.GamePlay.Netcode.Messages;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Explicit command wire schema, distinct from ECS component schemas. Identity/configuration
    /// must stay fixed; decoding must be deterministic and have no external side effects. Validate
    /// ranges, finiteness and allowed bits before returning true. The inbox also requires exact
    /// payload consumption. Advance the supplied reader; do not replace it with another stream.
    /// Unmanaged is NOT a wire layout: do not copy native struct memory,
    /// deserialize pointers, or retain borrowed packet storage in a decoded command.
    /// </summary>
    public interface ITickCommandCodec<TCommand> where TCommand : unmanaged
    {
        ushort CommandSchemaId { get; }
        ushort CodecRevision { get; }
        bool TryDecode(ref NetBitReader reader, out TCommand command);
    }
}
