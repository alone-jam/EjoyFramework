//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Network identity within a session/world epoch, not a local ECS handle. Both fields must
    /// be nonzero. Construction only copies values; the frame codec validates before use.
    /// A batch contains at most one generation of any NetworkId and is ordered by NetworkId.
    /// </summary>
    public readonly struct NetworkEntityKey
    {
        public uint NetworkId { get; }
        public uint SpawnGeneration { get; }

        public NetworkEntityKey(uint networkId, uint spawnGeneration)
        {
            NetworkId = networkId;
            SpawnGeneration = spawnGeneration;
        }
    }
}
