namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Borrowed, optional capture boundary after a tick's resolve and bounded despawn work.
    /// Called once per completed simulation tick, including each tick in a multi-tick frame.
    /// Capture copied rows into preallocated storage and publish a complete batch only after
    /// all rows succeed. Do not mutate the World here: this is a contract, not an enforced
    /// read-only view. Still-pending despawns must be excluded from the captured active set.
    /// </summary>
    public interface IEcsTickCompletedSink
    {
        void OnTickCompleted(World world, in EcsTime time);
    }
}
