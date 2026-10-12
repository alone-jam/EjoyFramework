using System;

namespace EjoyFramework.Core.Ecs
{
    internal interface IComponentPool
    {
        int Count { get; }
        int EntityAt(int index);
        bool Has(int entityIndex);
        bool Remove(int entityIndex);
        void Clear();
    }

    /// <summary>稀疏索引存放 dense index + 1，组件数据按类型连续存储。</summary>
    internal sealed class ComponentPool<T> : IComponentPool where T : struct
    {
        private int[] m_Sparse = Array.Empty<int>();
        private int[] m_Entities = Array.Empty<int>();
        private T[] m_Data = Array.Empty<T>();
        private ulong[] m_Lifetimes = Array.Empty<ulong>();
        private ulong m_LastLifetime;
        public int Count { get; private set; }

        public int EntityAt(int index) => m_Entities[index];
        public bool Has(int entityIndex) => entityIndex >= 0 && entityIndex < m_Sparse.Length && m_Sparse[entityIndex] != 0;
        public ref T Get(int entityIndex) => ref m_Data[m_Sparse[entityIndex] - 1];
        internal ulong GetLifetime(int entityIndex) => m_Lifetimes[m_Sparse[entityIndex] - 1];

        /// <summary>Reserves an exclusive entity-index bound and equally sized dense capacity; never changes Count.</summary>
        internal void EnsureCapacity(int capacity)
        {
            ValidateCapacity(capacity);
            EnsureStorage(capacity, capacity);
        }

        internal static void ValidateCapacity(int capacity)
        {
            // Conservative CLR array element-count ceiling, also usable on netstandard2.1
            // without Array.MaxLength. Actual allocations remain subject to platform/memory limits.
            const int maxArrayLength = 0x7FEFFFFF;
            if (capacity < 0 || capacity > maxArrayLength)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    "Capacity must be between 0 and 0x7FEFFFFF elements.");
        }

        public void Add(int entityIndex, T value)
        {
            if (m_LastLifetime == ulong.MaxValue)
                throw new OverflowException("Component membership lifetimes exhausted for this pool.");
            int nextCount = checked(Count + 1);
            int sparseCapacity = entityIndex >= m_Sparse.Length
                ? Grow(m_Sparse.Length, checked(entityIndex + 1)) : m_Sparse.Length;
            int denseCapacity = Count == m_Data.Length ? Grow(m_Data.Length, nextCount) : m_Data.Length;
            EnsureStorage(sparseCapacity, denseCapacity);
            ulong lifetime = m_LastLifetime + 1;
            m_Entities[Count] = entityIndex;
            m_Data[Count] = value;
            m_Lifetimes[Count] = lifetime;
            m_Sparse[entityIndex] = nextCount;
            Count = nextCount;
            m_LastLifetime = lifetime;
        }

        private void EnsureStorage(int sparseCapacity, int denseCapacity)
        {
            var sparse = m_Sparse;
            var entities = m_Entities;
            var data = m_Data;
            var lifetimes = m_Lifetimes;
            if (sparseCapacity > sparse.Length) Array.Resize(ref sparse, sparseCapacity);
            if (denseCapacity > entities.Length) Array.Resize(ref entities, denseCapacity);
            if (denseCapacity > data.Length) Array.Resize(ref data, denseCapacity);
            if (denseCapacity > lifetimes.Length) Array.Resize(ref lifetimes, denseCapacity);
            // Stage sparse plus ALL dense buffers: a failed allocation publishes nothing,
            // changes no membership and consumes no lifetime stamp.
            m_Sparse = sparse;
            m_Entities = entities;
            m_Data = data;
            m_Lifetimes = lifetimes;
        }

        public bool Remove(int entityIndex)
        {
            if (!Has(entityIndex)) return false;
            int removed = m_Sparse[entityIndex] - 1;
            int last = --Count;
            if (removed != last)
            {
                int moved = m_Entities[last];
                m_Entities[removed] = moved;
                m_Data[removed] = m_Data[last];
                m_Lifetimes[removed] = m_Lifetimes[last];
                m_Sparse[moved] = removed + 1;
            }
            m_Sparse[entityIndex] = 0;
            m_Data[last] = default;
            m_Entities[last] = 0;
            m_Lifetimes[last] = 0;
            return true;
        }

        public void Clear()
        {
            // Queries retain pool instances; world teardown must also release their capacity.
            m_Sparse = Array.Empty<int>();
            m_Entities = Array.Empty<int>();
            m_Data = Array.Empty<T>();
            m_Lifetimes = Array.Empty<ulong>();
            // Keep the lifetime counter monotonic even if the pool is cleared.
            Count = 0;
        }

        internal static int Grow(int capacity, int minimum)
        {
            return Math.Max(minimum, capacity <= int.MaxValue / 2 ? Math.Max(4, capacity * 2) : int.MaxValue);
        }
    }
}
