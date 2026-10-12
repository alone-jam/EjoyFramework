namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Copied diagnostics. Processed tick is published only after the complete peer batch commits, not packet acknowledgment.</summary>
    public readonly struct TickPeerDiagnostics
    {
        public TickPeerHandle Peer { get; }
        public bool IsConnected { get; }
        public bool IsAuthorized { get; }
        public bool RevocationPending { get; }
        public uint LastProcessedCommandTick { get; }
        public int PendingCommandCount { get; }
        public int PacketsThisFrame { get; }
        public int DataBytesThisFrame { get; }
        public int QuotaDropsThisFrame { get; }
        public TickCommandAdmissionResult LastAdmission { get; }

        internal TickPeerDiagnostics(TickPeerHandle peer, bool connected, bool authorized, bool pending,
            uint processedTick, int commands, int packets, int bytes, int drops, TickCommandAdmissionResult admission)
        {
            Peer = peer; IsConnected = connected; IsAuthorized = authorized; RevocationPending = pending;
            LastProcessedCommandTick = processedTick; PendingCommandCount = commands;
            PacketsThisFrame = packets; DataBytesThisFrame = bytes; QuotaDropsThisFrame = drops; LastAdmission = admission;
        }
    }
}
