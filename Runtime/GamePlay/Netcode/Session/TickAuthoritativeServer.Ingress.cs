using System;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    public sealed partial class TickAuthoritativeServer<TCommand, TCodec>
        where TCommand : unmanaged where TCodec : struct, ITickCommandCodec<TCommand>
    {
        private void ResetIngressBudgets()
        {
            m_PollEvents = m_PollBytes = 0;
            for (int i = 0; i < m_Peers.Length; i++)
            {
                m_Peers[i].Packets = m_Peers[i].Bytes = m_Peers[i].QuotaDrops = 0;
            }
        }

        private bool EnterTransportHandler(int dataBytes)
        {
            if (m_Disposed || m_Faulted || m_Terminating) return false;
            if (Environment.CurrentManagedThreadId != m_OwnerThread || !m_OwnedPoll || m_TickActive
                || m_HandlerDepth != 0 || m_ApplyingRevokes || m_Authorizing)
            {
                LatchFailure(new InvalidOperationException("Transport callbacks must originate from the exclusive server-owned poll."));
                return false;
            }
            if (m_PollEvents >= m_Options.MaxPollEvents || dataBytes > m_Options.MaxPollDataBytes - m_PollBytes)
            {
                LatchFailure(new InvalidOperationException("Transport exceeded its advertised poll budget."));
                return false;
            }
            m_PollEvents++;
            m_PollBytes += dataBytes;
            m_HandlerDepth++;
            return true;
        }

        private void HandleConnected(int connectionId)
        {
            if (!EnterTransportHandler(0)) return;
            try
            {
                if (connectionId < 0) throw new InvalidOperationException("Transport supplied a negative connection ID.");
                // Retire earlier disconnects before admitting any replacement ID. Otherwise
                // observers see ghost peers and a full server can reject a valid reconnect.
                ApplyPendingRevocations();
                ThrowLatched();
                int existing = FindConnected(connectionId);
                if (existing >= 0) throw new InvalidOperationException("Transport reused an active connection ID.");
                int index = -1;
                for (int i = 0; i < m_Peers.Length; i++)
                    if (!m_Peers[i].Connected && m_Peers[i].Generation != ulong.MaxValue) { index = i; break; }
                if (index < 0) { m_Transport.Kick(connectionId); return; }
                ulong generation = m_Peers[index].Generation + 1;
                m_Peers[index] = new PeerSlot { Generation = generation, ConnectionId = connectionId, Connected = true };
                m_ConnectedCount++;
                m_ConnectionNotification = true;
                m_NotificationDepth++;
                try { ConnectionObserved?.Invoke(HandleAt(index)); }
                finally { m_NotificationDepth--; m_ConnectionNotification = false; }
            }
            catch (Exception error) { LatchFailure(error); }
            finally { m_HandlerDepth--; }
        }

        private void HandleDisconnected(int connectionId)
        {
            if (!EnterTransportHandler(0)) return;
            try
            {
                int index = FindConnected(connectionId);
                if (index < 0) return;
                m_Peers[index].PendingRevoke = true;
                m_Peers[index].KickOnRemove = false;
                m_Peers[index].NotifyClosed = true;
            }
            catch (Exception error) { LatchFailure(error); }
            finally { m_HandlerDepth--; }
        }

        private void HandleData(int connectionId, ArraySegment<byte> payload)
        {
            if (!EnterTransportHandler(payload.Count)) return;
            try
            {
                int index = FindConnected(connectionId);
                if (index < 0) return;
                ref PeerSlot peer = ref m_Peers[index];
                // Untrusted traffic is globally charged, but never allocates an inbox or runs a codec.
                if (!peer.Authorized || peer.PendingRevoke) return;
                if (peer.Packets >= m_Options.MaxPeerPacketsPerFrame
                    || payload.Count > m_Options.MaxPeerDataBytesPerFrame - peer.Bytes)
                {
                    peer.QuotaDrops++;
                    return;
                }
                peer.Packets++;
                peer.Bytes += payload.Count;
                peer.LastAdmission = default; // A throwing codec must not leave a previous acceptance visible.
                peer.LastAdmission = peer.Inbox.TryAdmit(payload.AsSpan());
            }
            catch (Exception error) { LatchFailure(error); }
            finally { m_HandlerDepth--; }
        }
    }
}
