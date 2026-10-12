namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Input/Simulation/Resolve run each tick; Interpolation/Presentation run once each frame.
    /// Despawn runs only for captured cleanup batches at tick end and frame end. Creates and
    /// component writes still play back after each system; requested destruction waits for cleanup.
    /// </summary>
    public enum EcsPhase
    {
        TickInput = 0,
        TickSimulation = 1,
        TickResolve = 2,
        Despawn = 3,
        Interpolation = 4,
        Presentation = 5
    }
}
