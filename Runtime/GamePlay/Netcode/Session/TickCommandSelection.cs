//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Sealed tick decision and copied command. HasCommand=false deliberately chooses no fallback;
    /// the simulation owner decides missing-input policy. A default-valued admitted command still
    /// has HasCommand=true. Copies remain values, not live references into the inbox.
    /// </summary>
    public readonly struct TickCommandSelection<TCommand> where TCommand : unmanaged
    {
        public uint Tick { get; }
        public bool HasCommand { get; }
        public TCommand Command { get; }

        internal TickCommandSelection(uint tick, bool hasCommand, TCommand command)
        { Tick = tick; HasCommand = hasCommand; Command = hasCommand ? command : default; }
    }
}
