using System;

namespace EjoyFramework.Core.Streaming
{
    public sealed partial class EcsStreamingHandler
    {
        public void BeginLoad(int cellId, int layer, int cx, int cz, int contentKey, int lod)
        {
            RequireUsable();
            if (m_Inner == null) throw new InvalidOperationException("Set Inner before starting IO.");
            if (m_Operations.ContainsKey(cellId)) throw new InvalidOperationException("Cell already has an active operation.");
            long generation = checked(m_Generation + 1);
            // Validate metadata before reserving, then install the operation before any external IO.
            var validated = new CellPopulationContext(cellId, layer, cx, cz, contentKey, lod, generation, 0);
            int key = m_Loop.World.ReservePartitionKey();
            Operation operation;
            try
            {
                var context = new CellPopulationContext(validated.CellId, layer, cx, cz, contentKey, lod, generation, key);
                operation = new Operation(context);
                m_Operations.Add(cellId, operation);
                Schedule(operation);
                m_Generation = generation;
            }
            catch
            {
                m_Operations.Remove(cellId);
                m_World.ReleasePartitionKey(key);
                throw; // No IO has started, so the manager may safely treat this as terminal failure.
            }
            m_ExternalDepth++;
            try { m_Inner.BeginLoad(cellId, layer, cx, cz, contentKey, lod); }
            catch (Exception error)
            {
                if (IsCurrent(operation)) { operation.Cancelled = true; RecordFailure(operation, error); Schedule(operation); }
            }
            finally { m_ExternalDepth--; }
        }

        public void CancelLoad(int cellId)
        {
            RequireUsable();
            if (!m_Operations.TryGetValue(cellId, out var operation) || operation.Loaded || operation.Cancelled) return;
            operation.Cancelled = true;
            Schedule(operation);
            if (operation.LoadTerminal) return; // No in-flight load to cancel; drain any successful IO instead.
            m_ExternalDepth++;
            try { m_Inner.CancelLoad(cellId); }
            catch (Exception error) { if (IsCurrent(operation)) RecordFailure(operation, error); }
            finally { m_ExternalDepth--; }
        }

        public void BeginUnload(int cellId, int layer, int cx, int cz, int contentKey)
        {
            RequireUsable();
            if (!m_Operations.TryGetValue(cellId, out var operation) || !operation.Loaded)
                throw new InvalidOperationException("Cannot unload a cell before successful population notification.");
            if (operation.Unloading) return;
            operation.Unloading = true;
            Schedule(operation); // Always use the captured context for later cleanup and IO.
        }

        public void OnLodChanged(int cellId, int fromLod, int toLod)
        {
            RequireUsable();
            if (!m_Operations.TryGetValue(cellId, out var operation) || !operation.Loaded || operation.Unloading) return;
            m_ExternalDepth++;
            try { m_Inner.OnLodChanged(cellId, fromLod, toLod); }
            catch (Exception error) { if (IsCurrent(operation)) RecordFailure(operation, error); }
            finally { m_ExternalDepth--; }
        }

        public void NotifyLoaded(int cellId, bool success)
        {
            RequireUsable();
            if (!m_Operations.TryGetValue(cellId, out var operation) || operation.LoadTerminal) return;
            operation.LoadTerminal = true;
            operation.LoadSucceeded = success;
            if (!success) operation.Cancelled = true;
            Schedule(operation);
        }

        public void NotifyUnloaded(int cellId)
        {
            RequireUsable();
            if (!m_Operations.TryGetValue(cellId, out var operation) || !operation.UnloadStarted || operation.UnloadTerminal) return;
            operation.UnloadTerminal = true;
            Schedule(operation);
        }

        private void StartUnload(Operation operation)
        {
            operation.UnloadStarted = true; // Callback may complete synchronously, or IO may throw after starting.
            var context = operation.Context;
            m_ExternalDepth++;
            try { m_Inner.BeginUnload(context.CellId, context.Layer, context.Cx, context.Cz, context.ContentKey); }
            catch (Exception error) { if (IsCurrent(operation)) RecordFailure(operation, error); }
            finally { m_ExternalDepth--; }
        }

        private void PublishLoaded(Operation operation)
        {
            operation.Loaded = true;
            m_ExternalDepth++;
            try { m_Downstream.NotifyLoaded(operation.Context.CellId, true); }
            catch (Exception error) { RecordFailure(operation, error); }
            finally { m_ExternalDepth--; }
            // No state writes here: downstream may have synchronously requested unload.
        }

        private void Retire(Operation operation)
        {
            // Invalidate old queue tokens before releasing the empty key; restore on release failure.
            operation.Retired = true;
            try
            {
                if (!m_World.ReleasePartitionKey(operation.Context.PartitionKey))
                    throw new InvalidOperationException("The bridge's reserved partition key was released by another owner.");
            }
            catch { operation.Retired = false; throw; }
            m_Operations.Remove(operation.Context.CellId);
            m_ExternalDepth++;
            try
            {
                if (operation.Loaded) m_Downstream.NotifyUnloaded(operation.Context.CellId);
                else m_Downstream.NotifyLoaded(operation.Context.CellId, false);
            }
            catch (Exception error) { RecordFailure(operation, error); }
            finally { m_ExternalDepth--; }
        }
    }
}
