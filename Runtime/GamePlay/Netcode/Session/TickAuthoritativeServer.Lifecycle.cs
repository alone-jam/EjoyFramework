using System;
using System.Runtime.ExceptionServices;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    public sealed partial class TickAuthoritativeServer<TCommand, TCodec>
        where TCommand : unmanaged where TCodec : struct, ITickCommandCodec<TCommand>
    {
        /// <summary>Starts exclusive server ownership. Port zero is permitted by transports supporting ephemeral/local endpoints.</summary>
        public void Start(int port)
        {
            RequireOwner(); RequireNotDisposed();
            if (port < 0 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
            if (m_Started || m_Starting || m_Faulted || m_Running || m_NotificationDepth != 0 || m_Terminating)
                throw new InvalidOperationException("Server startup is single-use.");
            m_World.EnsureEntityCapacity(0);
            m_Starting = true;
            try
            {
                m_SubscribeAttempted = true; // Removal is attempted even if an event accessor throws halfway through.
                m_Transport.OnClientConnected += m_ConnectedHandler;
                m_Transport.OnClientDisconnected += m_DisconnectedHandler;
                m_Transport.OnData += m_DataHandler;
                m_Transport.StartServer(port);
                ThrowLatched();
                m_Started = true;
            }
            catch (Exception error)
            {
                LatchFailure(error);
                FaultShutdown();
                ThrowLatched();
                throw;
            }
            finally { m_Starting = false; }
        }

        public void Dispose()
        {
            RequireOwner();
            if (m_Disposed) return;
            if (m_Running || m_Starting || m_HandlerDepth != 0 || m_NotificationDepth != 0
                || m_Authorizing || m_Terminating || m_ApplyingRevokes)
                throw new InvalidOperationException("Cannot dispose during server dispatch or callbacks.");
            if (!m_World.IsDisposed) m_World.EnsureEntityCapacity(0);
            FaultShutdown();
            for (int i = 0; i < m_Peers.Length; i++) m_Peers[i].Inbox = null;
            m_Disposed = true;
            ConnectionObserved = null;
            PeerClosed = null;
            // Do not replace a primary simulation/startup exception with a later cleanup failure.
            if (m_Failure == null && m_CleanupFailure != null)
                ExceptionDispatchInfo.Capture(m_CleanupFailure).Throw();
        }

        private void FaultShutdown()
        {
            if (m_Terminating) return;
            m_Terminating = true;
            try
            {
                CloseAllInboxes();
                ClearActiveTick();
                Array.Clear(m_CloseNotes, 0, m_CloseNotes.Length);
                DetachTransport();
                if (!m_ShutdownAttempted)
                {
                    try { m_Transport.Shutdown(); m_ShutdownAttempted = true; }
                    catch (Exception error) { RecordCleanupFailure(error); }
                }
                try { m_Loop.Dispose(); }
                catch (Exception error) { RecordCleanupFailure(error); }
                m_Started = false;
                m_FaultCleanupDone = true;
            }
            finally { m_Terminating = false; }
        }

        private void CloseAllInboxes()
        {
            for (int i = 0; i < m_Peers.Length; i++)
            {
                var inbox = m_Peers[i].Inbox;
                if (inbox != null)
                {
                    try { if (inbox.IsTickInFlight) inbox.FailTick(m_CurrentTick); }
                    catch (Exception error) { RecordCleanupFailure(error); }
                    try { inbox.Close(); }
                    catch (Exception error) { RecordCleanupFailure(error); }
                }
                // Keep generation, last published acknowledgments and error diagnostics for inspection.
                m_Peers[i].Connected = m_Peers[i].Authorized = m_Peers[i].PendingRevoke = false;
                m_Peers[i].KickOnRemove = m_Peers[i].NotifyClosed = false;
            }
            m_ConnectedCount = m_AuthorizedCount = 0;
        }

        private void DetachTransport()
        {
            if (!m_SubscribeAttempted) return;
            bool complete = true;
            try { m_Transport.OnClientConnected -= m_ConnectedHandler; }
            catch (Exception error) { complete = false; RecordCleanupFailure(error); }
            try { m_Transport.OnClientDisconnected -= m_DisconnectedHandler; }
            catch (Exception error) { complete = false; RecordCleanupFailure(error); }
            try { m_Transport.OnData -= m_DataHandler; }
            catch (Exception error) { complete = false; RecordCleanupFailure(error); }
            m_SubscribeAttempted = !complete;
        }

        private void RecordCleanupFailure(Exception error)
        {
            if (m_CleanupFailure == null) m_CleanupFailure = error;
        }
    }
}
