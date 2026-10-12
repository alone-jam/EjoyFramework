using System;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    public sealed partial class TickAuthoritativeServer<TCommand, TCodec>
        where TCommand : unmanaged where TCodec : struct, ITickCommandCodec<TCommand>
    {
        private sealed class SelectionSystem : ISystem
        {
            private readonly TickAuthoritativeServer<TCommand, TCodec> m_Owner;
            internal SelectionSystem(TickAuthoritativeServer<TCommand, TCodec> owner) => m_Owner = owner;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => m_Owner.BeginPeerTick(in time);
        }

        private sealed class GuardedSystem : ISystem
        {
            private readonly TickAuthoritativeServer<TCommand, TCodec> m_Owner;
            private readonly ISystem m_System;
            internal GuardedSystem(TickAuthoritativeServer<TCommand, TCodec> owner, ISystem system)
            { m_Owner = owner; m_System = system; }
            public void Update(World world, CommandBuffer commands, in EcsTime time)
            {
                m_Owner.ThrowLatched();
                m_System.Update(world, commands, in time);
                m_Owner.ThrowLatched();
            }
        }

        private sealed class CompletionSink : IEcsTickCompletedSink
        {
            private readonly TickAuthoritativeServer<TCommand, TCodec> m_Owner;
            internal CompletionSink(TickAuthoritativeServer<TCommand, TCodec> owner) => m_Owner = owner;
            public void OnTickCompleted(World world, in EcsTime time) => m_Owner.CompletePeerTick(in time);
        }

        private void BeginPeerTick(in EcsTime time)
        {
            ThrowLatched();
            if (m_TickActive || time.IsReplay || m_LastCompletedTick == uint.MaxValue
                || time.Tick != m_LastCompletedTick + 1)
                throw new InvalidOperationException("Server tick must follow the last completed boundary exactly once.");
            m_CurrentTick = time.Tick;
            int count = 0;
            // Validate all participants before beginning any flight. No user callbacks occur here.
            for (int i = 0; i < m_Peers.Length; i++)
            {
                ref PeerSlot peer = ref m_Peers[i];
                if (!peer.Connected || !peer.Authorized || peer.PendingRevoke) continue;
                var inbox = peer.Inbox;
                if (inbox == null || inbox.IsClosed || inbox.IsFaulted || inbox.IsTickInFlight
                    || inbox.LastSimulatedTick != m_LastCompletedTick)
                    throw new InvalidOperationException("Peer inbox is not at the common completed tick.");
                m_ActiveIndices[count++] = i;
            }
            for (int i = 0; i < count; i++)
            {
                int index = m_ActiveIndices[i];
                var selection = m_Peers[index].Inbox.BeginTick(time.Tick);
                m_Active[i] = new TickPeerCommand<TCommand>(HandleAt(index), selection);
            }
            // A partial Begin failure publishes no ActiveCommands; fault cleanup scans every inbox.
            m_ActiveCount = count;
            m_TickActive = true;
        }

        private void CompletePeerTick(in EcsTime time)
        {
            ThrowLatched();
            if (!m_TickActive || m_CurrentTick != time.Tick)
                throw new InvalidOperationException("No matching server tick is active.");
            // Capture stages application data; it must not publish acknowledgments prematurely.
            m_Capture?.OnTickCompleted(m_World, in time);
            ThrowLatched();
            for (int i = 0; i < m_ActiveCount; i++)
            {
                int index = m_ActiveIndices[i];
                ref PeerSlot peer = ref m_Peers[index];
                if (HandleAt(index) != m_Active[i].Peer || !peer.Connected || !peer.Authorized
                    || peer.Inbox == null || peer.Inbox.IsClosed || peer.Inbox.IsFaulted
                    || !peer.Inbox.IsTickInFlight || peer.Inbox.LastSimulatedTick != m_LastCompletedTick)
                    throw new InvalidOperationException("Peer flight changed before the completion batch.");
            }
            // Complete every flight before exposing any new processed-command diagnostics.
            for (int i = 0; i < m_ActiveCount; i++) m_Peers[m_ActiveIndices[i]].Inbox.CompleteTick(time.Tick);
            for (int i = 0; i < m_ActiveCount; i++)
                if (m_Active[i].Selection.HasCommand) m_Peers[m_ActiveIndices[i]].ProcessedCommandTick = time.Tick;
            m_LastCompletedTick = time.Tick;
            ClearActiveTick();
            ApplyPendingRevocations(); // Outside World iteration, after the whole tick committed.
            ThrowLatched();
        }

        private void ClearActiveTick()
        {
            m_TickActive = false;
            m_ActiveCount = 0;
            m_CurrentTick = 0;
            Array.Clear(m_Active, 0, m_Active.Length);
            Array.Clear(m_ActiveIndices, 0, m_ActiveIndices.Length);
        }
    }
}
