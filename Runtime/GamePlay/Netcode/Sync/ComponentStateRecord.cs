//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Owned full-component value associated with a network entity generation. No borrowing of
    /// the receive buffer. A copied record survives receive-buffer and decode-scratch reuse.
    /// Construction does not validate the key or the state; the selected codec does so.
    /// </summary>
    public readonly struct ComponentStateRecord<TState> where TState : unmanaged
    {
        public NetworkEntityKey Entity { get; }
        public TState State { get; }

        public ComponentStateRecord(NetworkEntityKey entity, TState state)
        {
            Entity = entity;
            State = state;
        }
    }
}
