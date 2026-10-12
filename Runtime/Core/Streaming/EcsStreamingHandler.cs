using System;
using System.Collections.Generic;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Core.Streaming
{
    /// <summary>
    /// Single-thread IO-to-ECS bridge. Pump OUTSIDE systems before EcsLoop.RunFrame, never while
    /// that loop is running/faulted. Inner callbacks only record IO completion and queue work.
    /// Requires exactly-once, cellId-only IO callbacks; local generations protect queued work,
    /// not duplicate old IO callbacks after cellId reuse. Owns operation records/reserved keys,
    /// not the loop, world, populator or Inner. Per-load records/table growth are cold allocations;
    /// warmed Pump dispatch allocates nothing apart from user population, IO and notifications.
    /// </summary>
    public sealed partial class EcsStreamingHandler : IWorldStreamingHandler, IWorldStreamingNotifier, IDisposable
    {
        private sealed class Operation
        {
            public readonly CellPopulationContext Context;
            public bool Queued, Retired, LoadTerminal, LoadSucceeded, Cancelled, Loaded, Unloading;
            public bool UnloadStarted, UnloadTerminal;
            public int Cursor;
            public Exception Error;
            public Operation(CellPopulationContext context) { Context = context; }
        }
        private readonly struct Work
        {
            public readonly Operation Operation;
            public readonly long Generation;
            public Work(Operation operation) { Operation = operation; Generation = operation.Context.Generation; }
        }

        private readonly IWorldStreamingNotifier m_Downstream;
        private readonly EcsLoop m_Loop;
        private readonly World m_World;
        private readonly ICellEntityPopulator m_Populator;
        private readonly Dictionary<int, Operation> m_Operations = new Dictionary<int, Operation>();
        private readonly Queue<Work> m_Work = new Queue<Work>();
        private readonly int m_OwnerThreadId = Environment.CurrentManagedThreadId;
        private IWorldStreamingHandler m_Inner;
        private long m_Generation;
        private int m_ExternalDepth;
        private bool m_Pumping, m_Disposed;

        public EcsStreamingHandler(IWorldStreamingNotifier downstream, EcsLoop loop, ICellEntityPopulator populator)
        {
            m_Downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
            m_Loop = loop ?? throw new ArgumentNullException(nameof(loop));
            m_Populator = populator ?? throw new ArgumentNullException(nameof(populator));
            m_World = loop.World;
        }

        public IWorldStreamingHandler Inner
        {
            get { RequireUsable(); return m_Inner; }
            set
            {
                RequireIdle();
                if (ReferenceEquals(value, this)) throw new ArgumentException("Inner cannot be the bridge itself.", nameof(value));
                m_Inner = value;
            }
        }
        public int ActiveOperationCount { get { RequireUsable(); return m_Operations.Count; } }
        public Exception LastError { get; private set; }

        public bool TryGetContext(int cellId, out CellPopulationContext context)
        {
            RequireUsable();
            if (m_Operations.TryGetValue(cellId, out var operation)) { context = operation.Context; return true; }
            context = default; return false;
        }

        public bool TryGetFault(int cellId, out Exception error)
        {
            RequireUsable();
            error = m_Operations.TryGetValue(cellId, out var operation) ? operation.Error : null;
            return error != null;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            RequireIdle();
            m_Work.Clear(); // Only retired tokens can remain once no operations are active.
            m_Inner = null;
            m_Disposed = true;
        }

        private void RequireUsable()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThreadId)
                throw new InvalidOperationException("Use EcsStreamingHandler only on its constructing thread.");
            if (m_Disposed) throw new ObjectDisposedException(nameof(EcsStreamingHandler));
        }

        private void RequireIdle()
        {
            RequireUsable();
            if (m_Pumping || m_ExternalDepth != 0 || m_Operations.Count != 0)
                throw new InvalidOperationException("The bridge must have no active operations or external calls.");
        }

        private bool IsCurrent(Operation operation)
            => !operation.Retired && m_Operations.TryGetValue(operation.Context.CellId, out var current)
                && ReferenceEquals(current, operation) && current.Context.Generation == operation.Context.Generation;

        private void Schedule(Operation operation)
        {
            if (!IsCurrent(operation) || operation.Queued) return;
            m_Work.Enqueue(new Work(operation));
            operation.Queued = true;
        }

        private void RecordFailure(Operation operation, Exception error)
        {
            operation.Error = error;
            LastError = error;
            FrameworkLog.Error("EcsStreamingHandler cell {0}, generation {1}: {2}",
                operation.Context.CellId, operation.Context.Generation, error);
        }
    }
}
