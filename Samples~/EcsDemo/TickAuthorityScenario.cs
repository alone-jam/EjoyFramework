using System;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Netcode.Transport;

namespace EjoyGame.Samples.EcsDemo
{
    /// <summary>
    /// Two in-memory peers send real encoded commands. Only the server drives its World/clock.
    /// Local automatic authorization is sample setup, not a network authentication implementation.
    /// </summary>
    public sealed class TickAuthorityScenario : IDisposable
    {
        private struct Command { public uint Amount; }
        private struct PlayerState { public long Total; }
        private readonly struct Codec : ITickCommandCodec<Command>
        {
            public ushort CommandSchemaId => 0x402;
            public ushort CodecRevision => 1;
            public bool TryDecode(ref NetBitReader reader, out Command command)
            {
                command = default;
                if (!reader.TryReadBits(8, out uint amount) || amount > 10) return false;
                command = new Command { Amount = amount };
                return true;
            }
        }
        private sealed class Simulation : ISystem
        {
            private readonly TickAuthorityScenario m_Owner;
            internal Simulation(TickAuthorityScenario owner) => m_Owner = owner;
            public void Update(World world, CommandBuffer commands, in EcsTime time)
            {
                m_Owner.m_SimulationCalls++;
                foreach (var input in m_Owner.m_Server.ActiveCommands)
                {
                    // Explicit game policy: missing input contributes zero, not a repeated action.
                    if (!input.Selection.HasCommand) continue;
                    Entity entity = m_Owner.m_Entities[input.Peer.SlotIndex];
                    world.Get<PlayerState>(entity).Total += input.Selection.Command.Amount;
                }
            }
        }

        private readonly int m_OwnerThread = Environment.CurrentManagedThreadId;
        private readonly World m_World = new World();
        private readonly Entity[] m_Entities = new Entity[2];
        private readonly TickPeerHandle[] m_Peers = new TickPeerHandle[2];
        private readonly TickInputFrameContext[] m_Contexts = new TickInputFrameContext[2];
        private readonly byte[] m_Payload = new byte[1];
        private readonly byte[] m_Frame = new byte[33];
        private readonly INetTransport[] m_Clients;
        private readonly TickAuthoritativeServer<Command, Codec> m_Server;
        private int m_SimulationCalls;
        private bool m_Disposed;

        public TickAuthorityScenario()
        {
            var pair = LoopbackTransport.CreatePair();
            m_Clients = new[] { pair.client, LoopbackTransport.CreateClient(pair.server) };
            try
            {
                if (!(pair.server is IBudgetedNetTransport boundedServer))
                    throw new InvalidOperationException("The sample requires a budgeted server transport.");
                for (int i = 0; i < m_Entities.Length; i++)
                {
                    m_Entities[i] = m_World.CreateEntity();
                    m_World.Set(m_Entities[i], new PlayerState());
                }
                m_Server = new TickAuthoritativeServer<Command, Codec>(boundedServer, m_World, default,
                    new TickServerOptions(maxPeers: 2, commandWindow: 8, maxPayloadBits: 8,
                        tickRate: 8, maxFrameDelta: 1));
                m_Server.ConnectionObserved += AuthorizeLocalPeer;
                m_Server.AddSystem(EcsPhase.TickSimulation, new Simulation(this));
                m_Server.Start(0);
                m_Clients[0].Connect("local", 0);
                m_Clients[1].Connect("local", 0);
                m_Server.RunFrame(0); // Observes local connections, does not simulate a tick.
            }
            catch
            {
                if (m_Server != null) m_Server.Dispose();
                else pair.server.Shutdown();
                m_Clients[0].Shutdown(); m_Clients[1].Shutdown(); m_World.Dispose();
                throw;
            }
        }

        private void AuthorizeLocalPeer(TickPeerHandle peer)
        {
            int index = peer.SlotIndex;
            m_Peers[index] = peer;
            var context = new TickInputFrameContext((ulong)index + 1, 1, 0x402, 1);
            m_Contexts[index] = context;
            if (m_Server.AuthorizePeer(peer, in context) != TickPeerAuthorizationResult.Authorized)
                throw new InvalidOperationException("Local fixture authorization failed.");
        }

        public uint Tick { get { RequireUsable(); return m_Server.LastCompletedTick; } }
        public int SimulationCalls { get { RequireUsable(); return m_SimulationCalls; } }
        public long GetPlayerTotal(int peerIndex)
        {
            RequireUsable(); ValidatePeerIndex(peerIndex);
            return m_World.Get<PlayerState>(m_Entities[peerIndex]).Total;
        }
        public TickPeerDiagnostics GetPeer(int peerIndex)
        {
            RequireUsable(); ValidatePeerIndex(peerIndex);
            if (!m_Server.TryGetPeerDiagnostics(m_Peers[peerIndex], out var diagnostics))
                throw new InvalidOperationException("Sample peer is no longer available.");
            return diagnostics;
        }
        public void Send(int peerIndex, uint tick, byte amount)
        {
            RequireUsable(); ValidatePeerIndex(peerIndex);
            if (tick == 0 || amount > 10) throw new ArgumentOutOfRangeException(tick == 0 ? nameof(tick) : nameof(amount));
            var context = m_Contexts[peerIndex];
            var header = new TickInputFrameHeader(context.SessionEpoch, context.WorldEpoch, tick, 0x402, 1, 8);
            m_Payload[0] = amount;
            if (!TickInputFrameCodec.TryEncode(in header, m_Payload, m_Frame, out int written, out var error))
                throw new InvalidOperationException("Sample command encoding failed: " + error);
            m_Clients[peerIndex].Send(0, new ArraySegment<byte>(m_Frame, 0, written));
        }
        public int RunFrame(double serverDelta)
        {
            RequireUsable();
            return m_Server.RunFrame(serverDelta);
        }
        public void Dispose()
        {
            RequireOwner();
            if (m_Disposed) return;
            m_Disposed = true;
            try { m_Server.Dispose(); }
            finally
            {
                try { m_Clients[0].Shutdown(); }
                finally { try { m_Clients[1].Shutdown(); } finally { m_World.Dispose(); } }
            }
        }
        private static void ValidatePeerIndex(int index)
        { if ((uint)index >= 2u) throw new ArgumentOutOfRangeException(nameof(index)); }
        private void RequireOwner()
        { if (Environment.CurrentManagedThreadId != m_OwnerThread) throw new InvalidOperationException("Use the sample on its owner thread."); }
        private void RequireUsable()
        { RequireOwner(); if (m_Disposed) throw new ObjectDisposedException(nameof(TickAuthorityScenario)); }
    }
}
