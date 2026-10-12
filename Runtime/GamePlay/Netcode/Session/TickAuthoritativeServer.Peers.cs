using System;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    public sealed partial class TickAuthoritativeServer<TCommand, TCodec>
        where TCommand : unmanaged where TCodec : struct, ITickCommandCodec<TCommand>
    {
        /// <summary>
        /// Trusted-owner grant, not packet authentication. Context must be established externally
        /// with fresh epochs after reconnect/reassignment. No in-place authorized-context reset.
        /// </summary>
        public TickPeerAuthorizationResult AuthorizePeer(TickPeerHandle handle, in TickInputFrameContext context)
        {
            RequireOwner(); RequireNotDisposed();
            if (!m_Started || m_Faulted || m_Starting || m_Terminating || m_TickActive || m_Loop.IsRunning
                || m_ApplyingRevokes || m_Authorizing || (m_HandlerDepth != 0 && !m_ConnectionNotification))
                throw new InvalidOperationException("Authorize peers only at an idle owner boundary or connection notification.");
            if (!Matches(handle) || !m_Peers[handle.SlotIndex].Connected) return TickPeerAuthorizationResult.InvalidPeer;
            ref PeerSlot peer = ref m_Peers[handle.SlotIndex];
            if (peer.PendingRevoke) return TickPeerAuthorizationResult.RevocationPending;
            if (peer.Authorized) return TickPeerAuthorizationResult.AlreadyAuthorized;
            if (context.SessionEpoch == 0 || context.WorldEpoch == 0 || context.CommandSchemaId == 0 || context.CodecRevision == 0)
                return TickPeerAuthorizationResult.InvalidContext;
            if (context.CommandSchemaId != m_CommandSchemaId || context.CodecRevision != m_CodecRevision)
                return TickPeerAuthorizationResult.CodecMismatch;
            m_Authorizing = true;
            try
            {
                var inbox = new TickCommandInbox<TCommand, TCodec>(in context, m_Codec, m_LastCompletedTick,
                    m_Options.CommandWindow, m_Options.MaxPayloadBits);
                ThrowLatched();
                peer.Inbox = inbox;
                peer.Authorized = true;
                m_AuthorizedCount++;
                return TickPeerAuthorizationResult.Authorized;
            }
            finally { m_Authorizing = false; }
        }

        /// <summary>Revocation during simulation is deferred until the complete peer tick batch commits.</summary>
        public bool RevokePeer(TickPeerHandle handle)
        {
            RequireOwner(); RequireNotDisposed();
            if (m_Faulted || m_Terminating || m_Authorizing || (m_HandlerDepth != 0 && m_NotificationDepth == 0))
                throw new InvalidOperationException("Cannot revoke a peer from this callback or terminal state.");
            if (!m_Running && m_NotificationDepth == 0 && !m_ApplyingRevokes) m_World.EnsureEntityCapacity(0);
            if (!Matches(handle) || !m_Peers[handle.SlotIndex].Connected) return false;
            ref PeerSlot peer = ref m_Peers[handle.SlotIndex];
            peer.PendingRevoke = true;
            peer.KickOnRemove = true;
            peer.NotifyClosed = true;
            if (!m_Running && m_NotificationDepth == 0 && !m_ApplyingRevokes)
            {
                try { ApplyPendingRevocations(); ThrowLatched(); }
                catch (Exception error) { LatchFailure(error); FaultShutdown(); ThrowLatched(); throw; }
            }
            return true;
        }

        /// <summary>Closed-peer diagnostics remain readable until that slot is reused with a new generation.</summary>
        public bool TryGetPeerDiagnostics(TickPeerHandle handle, out TickPeerDiagnostics diagnostics)
        {
            RequireOwner();
            diagnostics = default;
            if (!Matches(handle)) return false;
            ref PeerSlot peer = ref m_Peers[handle.SlotIndex];
            diagnostics = new TickPeerDiagnostics(handle, peer.Connected, peer.Authorized, peer.PendingRevoke,
                peer.ProcessedCommandTick, peer.Inbox == null ? 0 : peer.Inbox.PendingCommandCount,
                peer.Packets, peer.Bytes, peer.QuotaDrops, peer.LastAdmission);
            return true;
        }

        private void ApplyPendingRevocations()
        {
            if (m_ApplyingRevokes || m_TickActive) return;
            m_ApplyingRevokes = true;
            try
            {
                // Notifications may request more revocations, but cannot add/authorize peers here.
                // Each pass closes at least one peer, so MaxPeers bounds the cascade.
                for (int pass = 0; pass < m_Peers.Length; pass++)
                {
                    int notes = ClosePendingBatch(out int kicks);
                    // All peer state is closed before calling transport or application code.
                    for (int i = 0; i < kicks; i++) m_Transport.Kick(m_KickNotes[i]);
                    ThrowLatched();
                    if (notes == 0 && kicks == 0) return;
                    for (int i = 0; i < notes; i++)
                    {
                        var handle = m_CloseNotes[i];
                        m_CloseNotes[i] = default;
                        m_NotificationDepth++;
                        try { PeerClosed?.Invoke(handle); }
                        catch (Exception error) { LatchFailure(error); }
                        finally { m_NotificationDepth--; }
                        ThrowLatched();
                    }
                }
            }
            finally { m_ApplyingRevokes = false; }
        }

        private int ClosePendingBatch(out int kicks)
        {
            int count = 0;
            kicks = 0;
            for (int i = 0; i < m_Peers.Length; i++)
            {
                ref PeerSlot peer = ref m_Peers[i];
                if (!peer.Connected || !peer.PendingRevoke) continue;
                if (peer.Inbox != null && peer.Inbox.IsTickInFlight)
                    throw new InvalidOperationException("Cannot remove a peer before its tick commits.");
                bool kick = peer.KickOnRemove;
                bool notify = peer.NotifyClosed;
                var handle = HandleAt(i);
                peer.Inbox?.Close();
                if (peer.Authorized) m_AuthorizedCount--;
                m_ConnectedCount--;
                peer.Connected = peer.Authorized = peer.PendingRevoke = peer.KickOnRemove = peer.NotifyClosed = false;
                if (notify) m_CloseNotes[count++] = handle;
                if (kick) m_KickNotes[kicks++] = handle.ConnectionId;
            }
            return count;
        }

        private bool Matches(TickPeerHandle handle)
        {
            if (handle.ServerInstanceId != m_ServerInstanceId || handle.Generation == 0
                || (uint)handle.SlotIndex >= (uint)m_Peers.Length) return false;
            ref PeerSlot peer = ref m_Peers[handle.SlotIndex];
            return peer.Generation == handle.Generation && peer.ConnectionId == handle.ConnectionId;
        }

        private TickPeerHandle HandleAt(int index)
            => new TickPeerHandle(m_ServerInstanceId, index, m_Peers[index].Generation, m_Peers[index].ConnectionId);

        private int FindConnected(int connectionId)
        {
            for (int i = 0; i < m_Peers.Length; i++)
                if (m_Peers[i].Connected && m_Peers[i].ConnectionId == connectionId) return i;
            return -1;
        }
    }
}
