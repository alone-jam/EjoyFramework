using System;
using System.Threading;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// World-bound latest authoritative values, owned by the constructing thread. Owns value
    /// copies, not the World; never writes live components or performs rollback. Mutations require
    /// a structural-safe point. Component membership stamps automatically invalidate access after
    /// removal/readdition; Bind again to accept a baseline for the new membership. Invalidation
    /// does not eagerly reclaim stored values: Unbind, rebind or ResetTimeline clears them.
    /// </summary>
    public sealed class AuthoritativeBaselineStore<T> : IDisposable where T : unmanaged
    {
        public readonly struct Binding
        {
            public Entity Entity { get; }
            internal long StoreId { get; }
            internal ulong Timeline { get; }
            internal ulong Generation { get; }

            internal Binding(Entity entity, long storeId, ulong timeline, ulong generation)
            {
                Entity = entity;
                StoreId = storeId;
                Timeline = timeline;
                Generation = generation;
            }
        }

        private struct Entry
        {
            public Entity Entity;
            public ulong Generation;
            public ulong ComponentLifetime;
            public uint Tick;
            public T Value;
        }

        private const int MaximumCapacity = 0x7FEFFFFF;
        private static long s_LastStoreId;
        private readonly long m_StoreId;
        private readonly int m_OwnerThreadId = Environment.CurrentManagedThreadId;
        private readonly World m_World;
        private Entry[] m_Entries;
        private ulong m_Timeline = 1;
        private ulong m_NextBindingGeneration;
        private bool m_Disposed;

        public int Capacity { get { RequireUsable(false); return m_Entries.Length; } }

        public AuthoritativeBaselineStore(World world, int initialCapacity = 64)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            world.RequireNotDisposed();
            ComponentPool<T>.ValidateCapacity(initialCapacity);
            m_Entries = new Entry[initialCapacity];
            m_StoreId = AllocateStoreId();
        }

        /// <summary>Reserves the exclusive entity-index bound [0, capacity), not an additional live count.</summary>
        public void EnsureCapacity(int capacity)
        {
            RequireUsable(true);
            ComponentPool<T>.ValidateCapacity(capacity);
            EnsureCapacityCore(capacity);
        }

        /// <summary>Every successful bind issues a fresh token and clears that entity's previous baseline.</summary>
        public Binding Bind(Entity entity)
        {
            RequireUsable(true);
            if (!TryGetAvailableLifetime(entity, out ulong lifetime))
                throw new InvalidOperationException("Bind requires a live, non-despawning entity with the component in this World.");
            if (m_NextBindingGeneration == ulong.MaxValue)
                throw new OverflowException("Baseline binding generations exhausted. Create a new store.");
            int required = checked(entity.Index + 1);
            ComponentPool<T>.ValidateCapacity(required);
            if (required > m_Entries.Length)
                EnsureCapacityCore(Math.Min(ComponentPool<T>.Grow(m_Entries.Length, required), MaximumCapacity));
            ulong generation = m_NextBindingGeneration + 1;
            m_Entries[entity.Index] = new Entry { Entity = entity, Generation = generation, ComponentLifetime = lifetime };
            m_NextBindingGeneration = generation;
            return new Binding(entity, m_StoreId, m_Timeline, generation);
        }

        /// <summary>May clear a matching token after its target disappeared; never clears a replacement binding.</summary>
        public bool Unbind(in Binding binding)
        {
            RequireUsable(true);
            if (!Matches(in binding)) return false;
            m_Entries[binding.Entity.Index] = default;
            return true;
        }

        /// <summary>
        /// Copies a newer, nonzero simulation tick. Ordering never wraps. Equal ticks retain the
        /// first value; every rejected write preserves storage. This does not commit a coherent
        /// multi-component snapshot, acknowledge input, or write the World.
        /// </summary>
        public BaselineWriteResult TrySet(in Binding binding, uint tick, in T value)
        {
            RequireUsable(true);
            if (tick == 0) throw new ArgumentOutOfRangeException(nameof(tick), "Authoritative tick zero is invalid.");
            if (!Matches(in binding)) return BaselineWriteResult.StaleBinding;
            ref Entry entry = ref m_Entries[binding.Entity.Index];
            if (!TargetAvailable(binding.Entity, entry.ComponentLifetime)) return BaselineWriteResult.TargetUnavailable;
            if (entry.Tick == tick) return BaselineWriteResult.DuplicateTick;
            if (entry.Tick > tick) return BaselineWriteResult.OlderTick;
            entry.Value = value;
            entry.Tick = tick;
            return BaselineWriteResult.Accepted;
        }

        public bool TryGet(in Binding binding, out uint tick, out T value)
        {
            RequireUsable(false);
            tick = 0;
            value = default;
            if (!Matches(in binding)) return false;
            ref Entry entry = ref m_Entries[binding.Entity.Index];
            if (!TargetAvailable(binding.Entity, entry.ComponentLifetime) || entry.Tick == 0) return false;
            tick = entry.Tick;
            value = entry.Value;
            return true;
        }

        /// <summary>
        /// Invalidates all tokens and values, retaining capacity. Old queued work must retain its
        /// original token; relabelling old network data with a current token defeats this boundary.
        /// Does not reset the World or its clock. Call Bind again for the new timeline.
        /// </summary>
        public void ResetTimeline()
        {
            RequireUsable(true);
            if (m_Timeline == ulong.MaxValue)
                throw new OverflowException("Baseline timelines exhausted. Create a new store.");
            Array.Clear(m_Entries, 0, m_Entries.Length);
            m_Timeline++;
        }

        public void Dispose()
        {
            RequireOwnerThread();
            if (m_Disposed) return;
            if (!m_World.IsDisposed) m_World.RequireStructuralChange();
            m_Entries = Array.Empty<Entry>();
            m_Disposed = true;
        }

        /// <summary>
        /// Read-only identity validation for companion state stores. Accepted means the binding
        /// targets the current membership, even if no authoritative value has been received.
        /// Does not impose authoritative tick ordering or require a stored baseline.
        /// </summary>
        internal BaselineWriteResult ValidateBinding(in Binding binding)
        {
            RequireUsable(false);
            if (!Matches(in binding)) return BaselineWriteResult.StaleBinding;
            return TargetAvailable(binding.Entity, m_Entries[binding.Entity.Index].ComponentLifetime)
                ? BaselineWriteResult.Accepted : BaselineWriteResult.TargetUnavailable;
        }

        private bool Matches(in Binding binding)
        {
            int index = binding.Entity.Index;
            if (binding.StoreId != m_StoreId || binding.Timeline != m_Timeline || binding.Generation == 0
                || (uint)index >= (uint)m_Entries.Length) return false;
            ref Entry entry = ref m_Entries[index];
            return entry.Generation == binding.Generation && entry.Entity == binding.Entity;
        }

        private bool TargetAvailable(Entity entity, ulong expectedLifetime)
            => TryGetAvailableLifetime(entity, out ulong lifetime) && lifetime == expectedLifetime;

        private bool TryGetAvailableLifetime(Entity entity, out ulong lifetime)
        {
            lifetime = 0;
            return m_World.IsAlive(entity) && !m_World.IsDespawning(entity)
                && m_World.TryGetComponentLifetime<T>(entity, out lifetime);
        }

        private void EnsureCapacityCore(int capacity)
        {
            ComponentPool<T>.ValidateCapacity(capacity);
            if (capacity <= m_Entries.Length) return;
            var entries = new Entry[capacity];
            Array.Copy(m_Entries, entries, m_Entries.Length);
            m_Entries = entries;
        }

        private void RequireUsable(bool mutation)
        {
            RequireOwnerThread();
            if (m_Disposed) throw new ObjectDisposedException(nameof(AuthoritativeBaselineStore<T>));
            if (mutation) m_World.RequireStructuralChange();
            else m_World.RequireNotDisposed();
        }

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThreadId)
                throw new InvalidOperationException("Use baseline storage only on its constructing thread.");
        }

        private static long AllocateStoreId()
        {
            while (true)
            {
                long current = Volatile.Read(ref s_LastStoreId);
                if (current == long.MaxValue) throw new OverflowException("Baseline store identifiers exhausted.");
                long next = current + 1;
                if (Interlocked.CompareExchange(ref s_LastStoreId, next, current) == current) return next;
            }
        }
    }
}
