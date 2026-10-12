using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Transport;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Opt-in authoritative owner of one bounded transport pump and private clock/loop. Borrows
    /// World, game systems and capture. They must be used exclusively: no legacy server, runner,
    /// EcsWorldComponent or other clock may also drive this World/transport. This is not an IO
    /// sandbox, packet authentication service, snapshot sender or rollback coordinator.
    /// Transport events contain only integer IDs: the transport MUST NOT reuse an ID while old
    /// events bearing it remain deliverable. Local handle generations cannot solve that ambiguity.
    /// </summary>
    public sealed partial class TickAuthoritativeServer<TCommand, TCodec> : IDisposable
        where TCommand : unmanaged where TCodec : struct, ITickCommandCodec<TCommand>
    {
        private struct PeerSlot
        {
            public ulong Generation;
            public int ConnectionId;
            public bool Connected, Authorized, PendingRevoke, KickOnRemove, NotifyClosed;
            public TickCommandInbox<TCommand, TCodec> Inbox;
            public uint ProcessedCommandTick;
            public int Packets, Bytes, QuotaDrops;
            public TickCommandAdmissionResult LastAdmission;
        }

        private readonly int m_OwnerThread = Environment.CurrentManagedThreadId;
        private readonly long m_ServerInstanceId;
        private readonly IBudgetedNetTransport m_Transport;
        private readonly World m_World;
        private readonly TickClock m_Clock;
        private readonly EcsLoop m_Loop;
        private readonly TickServerOptions m_Options;
        private readonly TCodec m_Codec;
        private readonly ushort m_CommandSchemaId, m_CodecRevision;
        private readonly IEcsTickCompletedSink m_Capture;
        private readonly PeerSlot[] m_Peers;
        private readonly TickPeerCommand<TCommand>[] m_Active;
        private readonly int[] m_ActiveIndices;
        private readonly TickPeerHandle[] m_CloseNotes;
        private readonly int[] m_KickNotes;
        private readonly Action<int> m_ConnectedHandler, m_DisconnectedHandler;
        private readonly Action<int, ArraySegment<byte>> m_DataHandler;
        private ExceptionDispatchInfo m_Failure;
        private Exception m_CleanupFailure;
        private volatile bool m_Faulted, m_Disposed;
        private bool m_Started, m_Starting, m_Running, m_OwnedPoll, m_TickActive;
        private bool m_SubscribeAttempted, m_ShutdownAttempted, m_FaultCleanupDone, m_Terminating;
        private bool m_ApplyingRevokes, m_ConnectionNotification, m_Authorizing;
        private int m_HandlerDepth, m_NotificationDepth, m_ActiveCount;
        private int m_ConnectedCount, m_AuthorizedCount, m_PollEvents, m_PollBytes;
        private uint m_CurrentTick, m_LastCompletedTick;

        /// <summary>Connection observation ONLY. Trusted owner may then authorize; this event is not authentication.</summary>
        public event Action<TickPeerHandle> ConnectionObserved;
        /// <summary>Raised after the whole removal batch is closed and any successful tick batch has committed.</summary>
        public event Action<TickPeerHandle> PeerClosed;
        public uint LastCompletedTick { get { RequireOwner(); return m_LastCompletedTick; } }
        /// <summary>Read-only clock observation; can exceed LastCompletedTick after failed execution.</summary>
        public uint ClockTick { get { RequireOwner(); return m_Clock.Tick; } }
        public bool IsFaulted { get { RequireOwner(); return m_Faulted; } }
        public bool IsStarted { get { RequireOwner(); return m_Started; } }
        public int ConnectedPeerCount { get { RequireOwner(); return m_ConnectedCount; } }
        public int AuthorizedPeerCount { get { RequireOwner(); return m_AuthorizedCount; } }
        /// <summary>First cleanup failure, if any. A throwing transport cannot be claimed fully shut down.</summary>
        public Exception CleanupFailure { get { RequireOwner(); return m_CleanupFailure; } }
        /// <summary>
        /// Borrowed tick-scoped selections in stable slot order. Valid only during this tick's
        /// systems/capture; never retain across a boundary. Empty outside a tick or after fault.
        /// </summary>
        public ReadOnlySpan<TickPeerCommand<TCommand>> ActiveCommands
        {
            get
            {
                RequireOwner();
                return m_TickActive && !m_Faulted ? m_Active.AsSpan(0, m_ActiveCount)
                    : ReadOnlySpan<TickPeerCommand<TCommand>>.Empty;
            }
        }

        public TickAuthoritativeServer(IBudgetedNetTransport transport, World world, TCodec codec,
            TickServerOptions options = null, IEcsTickCompletedSink capture = null)
        {
            m_Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_Options = options ?? new TickServerOptions();
            if (world.IsDisposed) throw new ObjectDisposedException(nameof(world));
            if (transport.IsClient) throw new ArgumentException("A client-role transport cannot be owned by this server.", nameof(transport));
            var identity = codec;
            m_CommandSchemaId = identity.CommandSchemaId;
            m_CodecRevision = identity.CodecRevision;
            if (m_CommandSchemaId == 0 || m_CodecRevision == 0) throw new ArgumentException("Codec identity must be nonzero.", nameof(codec));
            // Public no-growth guard validates structural safety without changing World capacity.
            world.EnsureEntityCapacity(0);
            m_Codec = codec; m_Capture = capture;
            m_ServerInstanceId = TickPeerHandle.AllocateServerInstanceId();
            m_Peers = new PeerSlot[m_Options.MaxPeers];
            m_Active = new TickPeerCommand<TCommand>[m_Options.MaxPeers];
            m_ActiveIndices = new int[m_Options.MaxPeers];
            m_CloseNotes = new TickPeerHandle[m_Options.MaxPeers];
            m_KickNotes = new int[m_Options.MaxPeers];
            m_Clock = new TickClock(m_Options.TickRate, m_Options.MaxTicksPerFrame, m_Options.MaxFrameDelta);
            m_Clock.Reset(m_Options.InitialTick);
            m_LastCompletedTick = m_Options.InitialTick;
            m_Loop = new EcsLoop(world, m_Clock);
            m_Loop.Add(EcsPhase.TickInput, new SelectionSystem(this), int.MinValue); // Always first; explicitly non-replayable.
            m_Loop.TickCompletedSink = new CompletionSink(this);
            m_ConnectedHandler = HandleConnected; m_DisconnectedHandler = HandleDisconnected; m_DataHandler = HandleData;
        }

        /// <summary>Setup only. Tick phases and Despawn are supported; borrowed systems are never disposed here.</summary>
        public void AddSystem(EcsPhase phase, ISystem system, int order = 0)
        {
            RequireOwner(); RequireNotDisposed();
            if (m_Started || m_Starting || m_Faulted || m_Running || m_Terminating)
                throw new InvalidOperationException("System registration is closed once startup begins.");
            if (system == null) throw new ArgumentNullException(nameof(system));
            if (phase != EcsPhase.TickInput && phase != EcsPhase.TickSimulation && phase != EcsPhase.TickResolve && phase != EcsPhase.Despawn)
                throw new ArgumentOutOfRangeException(nameof(phase), "The authoritative server does not register presentation systems.");
            m_Loop.Add(phase, new GuardedSystem(this, system), order); // Default replayable=false.
        }

        /// <summary>One bounded ingress pump followed by one EcsLoop.RunFrame. No receive-driven simulation.</summary>
        public int RunFrame(double serverDelta)
        {
            RequireOwner(); RequireNotDisposed();
            if (m_Running || m_Starting || m_NotificationDepth != 0 || m_HandlerDepth != 0
                || m_Terminating || m_Authorizing || m_ApplyingRevokes)
                throw new InvalidOperationException("Cannot reenter server dispatch.");
            if (double.IsNaN(serverDelta) || double.IsInfinity(serverDelta) || serverDelta < 0)
                throw new ArgumentOutOfRangeException(nameof(serverDelta));
            if (m_Faulted)
            {
                if (!m_FaultCleanupDone) { FaultShutdown(); ThrowLatched(); }
                throw new InvalidOperationException("Session faulted. Recreate it from a coherent baseline and fresh epochs.");
            }
            if (!m_Started) throw new InvalidOperationException("Start the server before running frames.");
            m_World.EnsureEntityCapacity(0); // Reject iteration/replay/disposal before pumping events.
            if (m_Clock.PendingTicks != 0 || m_Clock.Tick != m_LastCompletedTick)
                throw new InvalidOperationException("Private clock is not at the last completed boundary.");
            m_Running = true;
            try
            {
                ResetIngressBudgets();
                m_OwnedPoll = true;
                try { m_Transport.Poll(m_Options.MaxPollEvents, m_Options.MaxPollDataBytes); }
                finally { m_OwnedPoll = false; }
                ThrowLatched();
                ApplyPendingRevocations();
                ThrowLatched();
                int completed = m_Loop.RunFrame(serverDelta);
                ThrowLatched();
                ApplyPendingRevocations();
                ThrowLatched();
                return completed;
            }
            catch (Exception error)
            {
                LatchFailure(error);
                FaultShutdown();
                ThrowLatched();
                throw;
            }
            finally { m_OwnedPoll = false; m_Running = false; ClearActiveTick(); }
        }

        private void LatchFailure(Exception error)
        {
            Interlocked.CompareExchange(ref m_Failure, ExceptionDispatchInfo.Capture(error), null);
            m_Faulted = true;
        }
        private void ThrowLatched()
        {
            var failure = Volatile.Read(ref m_Failure);
            if (failure != null) failure.Throw();
            if (m_Faulted) throw new InvalidOperationException("The authoritative session is faulted.");
        }
        private void RequireOwner()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThread)
                throw new InvalidOperationException("Use the authoritative server on its constructing thread.");
        }
        private void RequireNotDisposed()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(TickAuthoritativeServer<TCommand, TCodec>));
        }
    }
}
