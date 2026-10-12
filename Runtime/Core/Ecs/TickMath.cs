namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Modular sequence arithmetic for snapshots. Ordering is meaningful only within less than
    /// 2^31 ticks. At exactly half range neither value is newer. Zero is the invalid/initial
    /// sentinel, but arithmetic treats it as an ordinary number; callers validate identities.
    /// TickClock itself never wraps; these helpers also support external sequence numbers.
    /// </summary>
    public static class TickMath
    {
        public const uint Invalid = 0;

        public static int Delta(uint newer, uint older) => unchecked((int)(newer - older));
        public static bool IsNewer(uint a, uint b) => Delta(a, b) > 0;
    }
}
