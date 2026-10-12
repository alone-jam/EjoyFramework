using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Immutable simulation/presentation time in seconds. Tick identifies the simulated tick,
    /// or the latest consumed tick for presentation. ElapsedTime is tick-grid time (excludes
    /// Alpha), not wall time. TickInterval zero denotes an unscheduled frame (no tick, alpha
    /// or replay state); default is a valid zero-duration frame. Use ForFrame for this case.
    /// No phase or scheduling policy is implied; safe to pass by readonly reference.
    /// </summary>
    public readonly struct EcsTime
    {
        public uint Tick { get; }
        public double DeltaTime { get; }
        public double TickInterval { get; }
        public double Alpha { get; }
        public double ElapsedTime { get; }
        public bool IsReplay { get; }

        /// <summary>Creates frame time without a tick clock; does not infer or advance simulation time.</summary>
        public static EcsTime ForFrame(double deltaTime) => new EcsTime(0, deltaTime, 0);

        public EcsTime(uint tick, double deltaTime, double tickInterval, double alpha = 0, bool isReplay = false)
        {
            if (!IsFinite(deltaTime) || deltaTime < 0)
                throw new ArgumentOutOfRangeException(nameof(deltaTime), "Use finite, nonnegative seconds.");
            if (!IsFinite(tickInterval) || tickInterval < 0 || !IsFinite(tick * tickInterval))
                throw new ArgumentOutOfRangeException(nameof(tickInterval), "Use a finite nonnegative interval with finite elapsed time.");
            if (tickInterval == 0 && (tick != 0 || alpha != 0 || isReplay))
                throw new ArgumentOutOfRangeException(nameof(tickInterval), "A tick, interpolation or replay requires a positive tick interval.");
            if (!IsFinite(alpha) || alpha < 0 || alpha >= 1)
                throw new ArgumentOutOfRangeException(nameof(alpha), "Interpolation alpha must be in [0, 1).");
            Tick = tick;
            DeltaTime = deltaTime;
            TickInterval = tickInterval;
            Alpha = alpha;
            ElapsedTime = tick * tickInterval;
            IsReplay = isReplay;
        }

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
