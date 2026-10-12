using System;

namespace EjoyFramework.Core.Streaming
{
    public sealed partial class EcsStreamingHandler
    {
        /// <summary>
        /// Shares both nonnegative budgets across queued generations. Each token present on entry
        /// is visited once; work scheduled by external calls waits until the next pump. Zero budgets
        /// still retire terminal operations. Despawn budget counts NEW requests, not physical releases.
        /// Call outside systems in manager.Update -> Pump -> manual driver.Step order.
        /// </summary>
        public void Pump(ref int createBudget, ref int despawnRequestBudget)
        {
            RequireUsable();
            if (m_Pumping || m_ExternalDepth != 0) throw new InvalidOperationException("Cannot reenter Pump from bridge/external callbacks.");
            if (m_Loop.IsRunning || m_Loop.IsFaulted) throw new InvalidOperationException("Pump requires a stopped, non-faulted ECS loop.");
            if (createBudget < 0) throw new ArgumentOutOfRangeException(nameof(createBudget));
            if (despawnRequestBudget < 0) throw new ArgumentOutOfRangeException(nameof(despawnRequestBudget));
            m_Loop.World.EnsureEntityCapacity(0); // Public no-growth loop/thread/world structural guard.
            m_Pumping = true;
            try
            {
                int queued = m_Work.Count;
                for (int i = 0; i < queued; i++)
                {
                    var token = m_Work.Dequeue();
                    var operation = token.Operation;
                    if (!IsCurrent(operation) || token.Generation != operation.Context.Generation) continue;
                    operation.Queued = false;
                    try { Process(operation, ref createBudget, ref despawnRequestBudget); }
                    catch (Exception error)
                    {
                        if (IsCurrent(operation))
                        {
                            operation.Cancelled = !operation.Loaded;
                            RecordFailure(operation, error);
                        }
                    }
                    if (IsCurrent(operation) && NeedsPump(operation)) Schedule(operation);
                }
            }
            finally { m_Pumping = false; }
        }

        private void Process(Operation operation, ref int createBudget, ref int despawnBudget)
        {
            if (operation.Cancelled || operation.Unloading)
            {
                Drain(operation, ref despawnBudget);
                return;
            }
            if (operation.Loaded || !operation.LoadTerminal || createBudget == 0) return;
            Populate(operation, ref createBudget);
            if (!IsCurrent(operation)) return;
            if (operation.Cancelled || operation.Unloading) Drain(operation, ref despawnBudget);
        }

        private void Populate(Operation operation, ref int createBudget)
        {
            int beforeBudget = createBudget;
            int cursor = operation.Cursor;
            bool complete;
            m_ExternalDepth++;
            try
            {
                complete = m_Populator.Populate(m_World, in operation.Context, ref createBudget, ref cursor);
                if (createBudget < 0 || createBudget > beforeBudget || cursor < operation.Cursor)
                    throw new InvalidOperationException("Populator must not increase/negate budget or decrease cursor.");
            }
            catch (Exception error)
            {
                createBudget = 0; // Consumption is unknown; never give this budget to another operation.
                if (IsCurrent(operation)) { operation.Cancelled = true; RecordFailure(operation, error); }
                return;
            }
            finally { m_ExternalDepth--; }
            if (!IsCurrent(operation)) return;
            operation.Cursor = cursor;
            if (complete && !operation.Cancelled) PublishLoaded(operation);
        }

        private void Drain(Operation operation, ref int despawnBudget)
        {
            int key = operation.Context.PartitionKey;
            if (despawnBudget > 0)
            {
                int granted = despawnBudget;
                // Reserve before issuing requests: a partial failure must not lend this budget
                // to later operations. Only a successful return can refund the unused amount.
                despawnBudget = 0;
                int issued = m_World.RequestDespawnPartition(key, granted);
                despawnBudget = granted - issued;
            }
            // Pending entities count until actual EcsLoop cleanup; never clear membership to fake completion.
            if (m_World.GetPartitionCount(key) != 0 || !operation.LoadTerminal) return;
            if (operation.LoadSucceeded)
            {
                if (!operation.UnloadStarted) StartUnload(operation);
                if (!IsCurrent(operation) || !operation.UnloadTerminal) return;
            }
            Retire(operation);
        }

        private bool NeedsPump(Operation operation)
        {
            if (operation.Cancelled || operation.Unloading)
                return m_World.GetPartitionCount(operation.Context.PartitionKey) != 0
                    || (operation.LoadTerminal && (!operation.LoadSucceeded || !operation.UnloadStarted || operation.UnloadTerminal));
            return !operation.Loaded && operation.LoadTerminal;
        }
    }
}
