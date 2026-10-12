using System;
using System.Collections.Generic;
using System.Threading;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// 独立、单线程的 ECS 世界。组件 ref 只能使用到下一次结构变更前。
    /// ForEach/SystemGroup 执行期间禁止直接增删；使用 CommandBuffer 延迟提交。
    /// Explicit replay additionally freezes topology, reservations and despawn state, including
    /// outside iteration; only existing component values may change under that guard.
    /// </summary>
    public sealed partial class World : IDisposable
    {
        private struct Slot
        {
            public uint Version;
            public int DenseIndex;
            public int NextFree;
            public byte State; // 0 free, 1 reserved, 2 alive, 3 retired
            public byte DespawnState; // 0 none, 1 pending, 2 selected for the current cleanup batch
            public bool HasPartition;
            public int PartitionKey;
            public int PartitionPrevious;
            public int PartitionNext;
            public CommandBuffer Owner;
        }

        private static long s_NextId;
        private Slot[] m_Slots;
        private int[] m_Alive;
        private int m_SlotCount;
        private int m_FreeHead = -1;
        private int m_IterationDepth;
        private bool m_Replaying;
        internal bool IsReplaying => m_Replaying;
        private IComponentPool[] m_Pools = Array.Empty<IComponentPool>();
        private readonly List<IComponentPool> m_RegisteredPools = new List<IComponentPool>();

        public long Id { get; }
        public int EntityCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public World(int initialCapacity = 64)
        {
            if (initialCapacity < 1) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            Id = Interlocked.Increment(ref s_NextId);
            if (Id <= 0) throw new InvalidOperationException("World identifiers exhausted.");
            m_Slots = new Slot[initialCapacity];
            m_Alive = new int[initialCapacity];
        }

        /// <summary>
        /// Reserves storage for the absolute entity-index range [0, capacity), not additional
        /// live entities. Includes slot and alive arrays only; does not grow component pools or
        /// despawn/command queues. Never shrinks or creates entities. Structural-safe points only.
        /// </summary>
        public void EnsureEntityCapacity(int capacity)
        {
            RequireStructuralChange();
            ComponentPool<int>.ValidateCapacity(capacity);
            // Publish both buffers only after all allocations succeed; handles/counts are untouched.
            var slots = m_Slots;
            var alive = m_Alive;
            if (capacity > slots.Length) Array.Resize(ref slots, capacity);
            if (capacity > alive.Length) Array.Resize(ref alive, capacity);
            m_Slots = slots;
            m_Alive = alive;
        }

        /// <summary>
        /// Reserves T's sparse index range [0, capacity) and dense storage for at least capacity
        /// values. This is an absolute index bound, not the number of T values to add. Does not
        /// reserve entity arrays; use EnsureEntityCapacity as well before bulk creation. No shrink.
        /// May invalidate component refs, so even zero/no-growth requests require a structural-safe point.
        /// </summary>
        public void EnsureCapacity<T>(int capacity) where T : struct
        {
            RequireStructuralChange();
            ComponentPool<T>.ValidateCapacity(capacity);
            if (capacity == 0) return;
            GetPool<T>().EnsureCapacity(capacity);
        }

        public Entity CreateEntity()
        {
            RequireStructuralChange();
            var entity = ReserveEntity(null);
            Activate(entity, null);
            return entity;
        }

        public bool IsAlive(Entity entity) => Matches(entity) && m_Slots[entity.Index].State == 2;

        /// <summary>Explicit immediate structural destruction; bypasses despawn cleanup systems.</summary>
        public void DestroyEntity(Entity entity)
        {
            RequireAlive(entity);
            RequireStructuralChange();
            foreach (var pool in m_RegisteredPools) pool.Remove(entity.Index);
            int denseIndex = m_Slots[entity.Index].DenseIndex;
            int moved = m_Alive[--EntityCount];
            if (denseIndex != EntityCount)
            {
                m_Alive[denseIndex] = moved;
                m_Slots[moved].DenseIndex = denseIndex;
            }
            m_Alive[EntityCount] = 0;
            if (m_Slots[entity.Index].DespawnState != 0) PendingDespawnCount--;
            ReleaseSlot(entity.Index);
        }

        /// <summary>插入或覆盖组件。覆盖已有值不是结构变更。</summary>
        public void Set<T>(Entity entity, T value) where T : struct
        {
            RequireAlive(entity);
            var pool = FindPool<T>();
            if (pool != null && pool.Has(entity.Index)) pool.Get(entity.Index) = value;
            else
            {
                // Reject missing membership before GetPool could publish a new empty pool.
                RequireStructuralChange();
                GetPool<T>().Add(entity.Index, value);
            }
        }

        /// <summary>返回原位引用；禁止跨结构变更、World.Dispose 或线程保存此引用。</summary>
        public ref T Get<T>(Entity entity) where T : struct
        {
            RequireAlive(entity);
            var pool = FindPool<T>();
            if (pool == null || !pool.Has(entity.Index))
                throw new InvalidOperationException($"{entity} has no {typeof(T).Name} component.");
            return ref pool.Get(entity.Index);
        }

        public bool Has<T>(Entity entity) where T : struct
        {
            RequireNotDisposed();
            if (!IsAlive(entity)) return false;
            var pool = FindPool<T>();
            return pool != null && pool.Has(entity.Index);
        }

        /// <summary>
        /// Opaque component membership identity, unchanged by value writes and capacity growth.
        /// Includes pending-despawn entities while their component still exists. Missing targets
        /// return false/zero without creating a pool. Not a tick, change version or network ID.
        /// </summary>
        public bool TryGetComponentLifetime<T>(Entity entity, out ulong lifetime) where T : struct
        {
            RequireNotDisposed();
            lifetime = 0;
            if (!IsAlive(entity)) return false;
            var pool = FindPool<T>();
            if (pool == null || !pool.Has(entity.Index)) return false;
            lifetime = pool.GetLifetime(entity.Index);
            return true;
        }

        public bool TryGet<T>(Entity entity, out T value) where T : struct
        {
            RequireNotDisposed();
            if (IsAlive(entity))
            {
                var pool = FindPool<T>();
                if (pool != null && pool.Has(entity.Index))
                {
                    value = pool.Get(entity.Index);
                    return true;
                }
            }
            value = default;
            return false;
        }

        public bool Remove<T>(Entity entity) where T : struct
        {
            RequireAlive(entity);
            RequireNotReplaying();
            var pool = FindPool<T>();
            if (pool == null || !pool.Has(entity.Index)) return false;
            RequireStructuralChange();
            return pool.Remove(entity.Index);
        }

        public Query Query()
        {
            RequireNotDisposed();
            return new Query(this);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            RequireStructuralChange();
            IsDisposed = true;
            foreach (var pool in m_RegisteredPools) pool.Clear();
            m_RegisteredPools.Clear();
            m_RegisteredPools.Capacity = 0;
            m_Pools = Array.Empty<IComponentPool>();
            ClearDespawnState();
            m_Partitions = null;
            m_Slots = Array.Empty<Slot>();
            m_Alive = Array.Empty<int>();
            EntityCount = 0;
        }

        internal ComponentPool<T> GetPool<T>() where T : struct
        {
            RequireNotDisposed();
            int id = ComponentType<T>.Id;
            if (id < m_Pools.Length && m_Pools[id] != null) return (ComponentPool<T>)m_Pools[id];
            RequireNotReplaying(); // Even an empty new pool changes the World's storage topology.

            var pool = new ComponentPool<T>();
            var pools = m_Pools;
            if (id >= pools.Length)
            {
                int capacity = Math.Min(ComponentPool<int>.Grow(pools.Length, id + 1), ComponentType.MaxTypeCount);
                Array.Resize(ref pools, capacity);
            }
            // List.Add is the last allocation-capable step. Until it succeeds, neither the
            // table entry nor its replacement array is published. The remaining writes cannot allocate.
            m_RegisteredPools.Add(pool);
            pools[id] = pool;
            m_Pools = pools;
            return pool;
        }

        private ComponentPool<T> FindPool<T>() where T : struct
        {
            int id = ComponentType<T>.Id;
            return id < m_Pools.Length ? (ComponentPool<T>)m_Pools[id] : null;
        }

        internal int AliveIndexAt(int denseIndex) => m_Alive[denseIndex];
        internal Entity EntityAt(int index) => new Entity(Id, index, m_Slots[index].Version);

        internal void EnterIteration()
        {
            RequireNotDisposed();
            m_IterationDepth++;
        }
        internal void ExitIteration() => m_IterationDepth--;

        internal void RequireNotDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(World));
        }

        /// <summary>For external state recording that is nonstructural but must not interleave with traversal.</summary>
        internal void RequireOutsideIteration()
        {
            RequireNotDisposed();
            if (m_IterationDepth != 0)
                throw new InvalidOperationException("This operation must run outside World iteration.");
        }

        /// <summary>Reject topology and lifecycle changes even outside iteration during value-only replay.</summary>
        internal void RequireNotReplaying()
        {
            RequireNotDisposed();
            if (m_Replaying) throw new InvalidOperationException("Only existing component values may change during replay.");
        }

        internal void BeginReplay()
        {
            RequireStructuralChange();
            m_Replaying = true;
        }

        internal void EndReplay() => m_Replaying = false;

        internal void RequireStructuralChange()
        {
            RequireNotReplaying();
            if (m_IterationDepth != 0)
                throw new InvalidOperationException("Structural changes are forbidden during iteration. Use a CommandBuffer.");
        }

        internal void ValidateCommandTarget(Entity entity, CommandBuffer owner)
        {
            RequireNotDisposed();
            if (IsAlive(entity)) return;
            if (Matches(entity) && m_Slots[entity.Index].State == 1 && ReferenceEquals(m_Slots[entity.Index].Owner, owner)) return;
            throw new InvalidOperationException("Command target is stale, belongs to another world, or is reserved by another buffer.");
        }

        internal Entity ReserveEntity(CommandBuffer owner)
        {
            RequireNotReplaying();
            int index;
            if (m_FreeHead >= 0)
            {
                index = m_FreeHead;
                m_FreeHead = m_Slots[index].NextFree;
            }
            else
            {
                if (m_SlotCount == m_Slots.Length)
                    Array.Resize(ref m_Slots, ComponentPool<int>.Grow(m_Slots.Length, checked(m_SlotCount + 1)));
                index = m_SlotCount++;
                m_Slots[index].Version = 1;
            }
            m_Slots[index].State = 1;
            m_Slots[index].Owner = owner;
            return EntityAt(index);
        }

        internal void Activate(Entity entity, CommandBuffer owner)
        {
            RequireStructuralChange();
            ValidateCommandTarget(entity, owner);
            if (m_Slots[entity.Index].State != 1) throw new InvalidOperationException("Entity already activated.");
            if (EntityCount == m_Alive.Length)
                Array.Resize(ref m_Alive, ComponentPool<int>.Grow(m_Alive.Length, checked(EntityCount + 1)));
            m_Slots[entity.Index].State = 2;
            m_Slots[entity.Index].Owner = null;
            m_Slots[entity.Index].DenseIndex = EntityCount;
            m_Alive[EntityCount++] = entity.Index;
        }

        internal void CancelReservation(Entity entity, CommandBuffer owner)
        {
            // Teardown after World disposal remains harmless; live replay cannot release slots.
            if (!IsDisposed) RequireNotReplaying();
            if (Matches(entity) && m_Slots[entity.Index].State == 1 && ReferenceEquals(m_Slots[entity.Index].Owner, owner))
                ReleaseSlot(entity.Index);
        }

        private bool Matches(Entity entity) => !IsDisposed && entity.WorldId == Id && entity.Index >= 0
            && entity.Index < m_SlotCount && entity.Version == m_Slots[entity.Index].Version;

        private void RequireAlive(Entity entity)
        {
            RequireNotDisposed();
            if (!IsAlive(entity)) throw new InvalidOperationException("Entity is stale, reserved, or belongs to another world.");
        }

        private void ReleaseSlot(int index)
        {
            DetachPartition(index);
            m_Slots[index].Owner = null;
            m_Slots[index].DespawnState = 0;
            // A retired slot is never reused: generation wrap cannot resurrect an old handle.
            if (m_Slots[index].Version == uint.MaxValue)
            {
                m_Slots[index].State = 3;
                return;
            }
            m_Slots[index].Version++;
            m_Slots[index].State = 0;
            m_Slots[index].NextFree = m_FreeHead;
            m_FreeHead = index;
        }
    }
}
