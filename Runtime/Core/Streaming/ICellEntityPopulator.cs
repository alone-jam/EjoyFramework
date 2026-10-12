using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Core.Streaming
{
    /// <summary>
    /// Incremental, single-thread population. Return true only when the entire generation is ready.
    /// Assign EVERY created entity to context.PartitionKey immediately after CreateEntity, BEFORE
    /// any initialization that can throw; initialize it before returning. Decrement the shared
    /// budget for every created entity, never increase it or make it negative. Cursor starts at
    /// zero, is operation-local and must not decrease. Do not retain refs, dispose the borrowed
    /// world, mutate other generations or release the bridge's reserved key. Never clear/move
    /// owned partition membership to signal completion: it remains until physical destruction.
    /// Partial entities are visible: gate gameplay through a readiness component/query convention
    /// until population completes. Delaying NotifyLoaded alone does NOT hide partial entities.
    /// </summary>
    public interface ICellEntityPopulator
    {
        bool Populate(World world, in CellPopulationContext context, ref int remainingCreateBudget, ref int cursor);
    }
}
