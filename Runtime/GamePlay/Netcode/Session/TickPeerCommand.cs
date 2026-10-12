namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Copied peer identity and sealed tick selection. Missing-input policy belongs to game systems.</summary>
    public readonly struct TickPeerCommand<TCommand> where TCommand : unmanaged
    {
        public TickPeerHandle Peer { get; }
        public TickCommandSelection<TCommand> Selection { get; }
        internal TickPeerCommand(TickPeerHandle peer, TickCommandSelection<TCommand> selection)
        { Peer = peer; Selection = selection; }
    }
}
