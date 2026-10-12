using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Fixed-step clock owned by its constructing thread. All access requires that thread;
    /// mutations verify ownership. Configuration is immutable. Steady-state calls allocate nothing.
    /// Accumulate once per frame, then drain TryAdvance before presenting or accumulating again.
    /// Consumption advances time, not execution of a system: the caller handles simulation failures.
    /// </summary>
    public sealed class TickClock
    {
        private readonly int m_OwnerThreadId = Environment.CurrentManagedThreadId;
        private double m_Fraction;

        public int TickRate { get; }
        public double TickInterval { get; }
        public int MaxTicksPerFrame { get; }
        public double MaxFrameDelta { get; }
        public uint Tick { get; private set; }
        public int PendingTicks { get; private set; }

        /// <summary>
        /// Remainder in [0, 1). Use for previous/current state interpolation only after the batch
        /// is drained; while ticks are pending this describes the eventual frame remainder.
        /// </summary>
        public double Alpha => m_Fraction;

        /// <summary>
        /// Whole ticks discarded by the catch-up budget after frame-delta clamping. Does not
        /// count wall time removed by MaxFrameDelta. Saturates at long.MaxValue until Reset.
        /// </summary>
        public long DroppedTicks { get; private set; }

        public TickClock(int tickRate = 30, int maxTicksPerFrame = 4, double maxFrameDelta = 0.25)
        {
            if (tickRate < 1 || tickRate > 1000)
                throw new ArgumentOutOfRangeException(nameof(tickRate), "Tick rate must be between 1 and 1000 Hz.");
            if (maxTicksPerFrame < 1)
                throw new ArgumentOutOfRangeException(nameof(maxTicksPerFrame), "Allow at least one tick per frame.");
            if (!EcsTime.IsFinite(maxFrameDelta) || maxFrameDelta <= 0 || !EcsTime.IsFinite(maxFrameDelta * tickRate))
                throw new ArgumentOutOfRangeException(nameof(maxFrameDelta), "Use positive finite seconds representable in tick units.");
            TickRate = tickRate;
            TickInterval = 1.0 / tickRate;
            MaxTicksPerFrame = maxTicksPerFrame;
            MaxFrameDelta = maxFrameDelta;
        }

        /// <summary>
        /// Accepts finite nonnegative seconds, clamps to MaxFrameDelta and prepares at most
        /// MaxTicksPerFrame ticks. Excess whole ticks are discarded; the fractional tick survives.
        /// Tick does not change. Rejects an unfinished batch without modifying it. No epsilon
        /// is added at tick boundaries: only a complete tick in double arithmetic becomes due.
        /// </summary>
        public int Accumulate(double realDeltaSeconds)
        {
            RequireOwnerThread();
            if (!EcsTime.IsFinite(realDeltaSeconds) || realDeltaSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(realDeltaSeconds), "Use finite, nonnegative seconds.");
            if (PendingTicks != 0)
                throw new InvalidOperationException("Drain pending ticks with TryAdvance or explicitly Reset before accumulating.");

            // Work in tick units to avoid repeated subtraction of an inexact seconds interval.
            double ticks = Math.Min(realDeltaSeconds, MaxFrameDelta) * TickRate;
            double wholeTicks = Math.Floor(ticks);
            // Split first so a very large frame cannot erase the previous fractional tick.
            double fraction = ticks - wholeTicks + m_Fraction;
            if (fraction >= 1)
            {
                wholeTicks += 1;
                fraction -= 1;
            }
            m_Fraction = fraction;
            PendingTicks = (int)Math.Min(wholeTicks, MaxTicksPerFrame);
            double dropped = wholeTicks - PendingTicks;
            DroppedTicks = dropped >= long.MaxValue - DroppedTicks
                ? long.MaxValue
                : DroppedTicks + (long)dropped;
            return PendingTicks;
        }

        /// <summary>
        /// Consumes one pending tick and returns its fixed-step snapshot (Alpha = 0).
        /// Empty batches return false/default. Never wraps into tick zero: overflow throws
        /// without consuming the pending tick; Reset to an explicit baseline to recover.
        /// </summary>
        public bool TryAdvance(out EcsTime time)
        {
            RequireOwnerThread();
            time = default;
            if (PendingTicks == 0) return false;
            if (Tick == uint.MaxValue)
                throw new OverflowException("Tick sequence exhausted. Reset the clock to an explicit baseline before advancing.");
            time = new EcsTime(Tick + 1, TickInterval, TickInterval);
            Tick++;
            PendingTicks--;
            return true;
        }

        /// <summary>Sets the baseline tick; clears pending work, fraction and drop diagnostics.</summary>
        public void Reset(uint tick = TickMath.Invalid)
        {
            RequireOwnerThread();
            Tick = tick;
            PendingTicks = 0;
            m_Fraction = 0;
            DroppedTicks = 0;
        }

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThreadId)
                throw new InvalidOperationException("Use TickClock only on its constructing thread.");
        }
    }
}
