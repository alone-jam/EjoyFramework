namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Owner authorization/grant result. No method here authenticates a packet or remote peer.</summary>
    public enum TickPeerAuthorizationResult : byte
    {
        NotAuthorized,
        Authorized,
        InvalidPeer,
        InvalidContext,
        CodecMismatch,
        AlreadyAuthorized,
        RevocationPending
    }
}
