using System;
using System.Threading;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Local server-instance + slot-generation identity, not an authentication credential or wire entity ID.</summary>
    public readonly struct TickPeerHandle : IEquatable<TickPeerHandle>
    {
        // Non-generic allocator: different server TCommand/TCodec instantiations must never collide.
        private static long s_LastServerInstanceId;
        public long ServerInstanceId { get; }
        public int SlotIndex { get; }
        public ulong Generation { get; }
        public int ConnectionId { get; }
        internal TickPeerHandle(long serverInstanceId, int slotIndex, ulong generation, int connectionId)
        { ServerInstanceId = serverInstanceId; SlotIndex = slotIndex; Generation = generation; ConnectionId = connectionId; }

        public bool Equals(TickPeerHandle other) => ServerInstanceId == other.ServerInstanceId
            && SlotIndex == other.SlotIndex && Generation == other.Generation && ConnectionId == other.ConnectionId;
        public override bool Equals(object obj) => obj is TickPeerHandle other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return (((ServerInstanceId.GetHashCode() * 397) ^ SlotIndex) * 397 ^ Generation.GetHashCode()) * 397 ^ ConnectionId; }
        }
        public static bool operator ==(TickPeerHandle left, TickPeerHandle right) => left.Equals(right);
        public static bool operator !=(TickPeerHandle left, TickPeerHandle right) => !left.Equals(right);
        internal static long AllocateServerInstanceId()
        {
            while (true)
            {
                long previous = Volatile.Read(ref s_LastServerInstanceId);
                if (previous == long.MaxValue) throw new OverflowException("Tick server identities exhausted.");
                if (Interlocked.CompareExchange(ref s_LastServerInstanceId, previous + 1, previous) == previous)
                    return previous + 1;
            }
        }
    }
}
