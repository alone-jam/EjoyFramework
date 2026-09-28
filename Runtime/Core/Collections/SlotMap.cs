//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core
{
    /// <summary>
    /// <see cref="SlotMap{T}"/> 的代际句柄。default 无效；元素移除后旧句柄的代数不再匹配，查找返回"不存在"。
    /// 可用 <see cref="Packed"/> / <see cref="FromPacked"/> 与 64 位整数互转（存档、网络、日志）。
    /// 句柄不带元素类型：不同 SlotMap 的句柄不要混用。
    /// </summary>
    public readonly struct SlotHandle : IEquatable<SlotHandle>
    {
        private readonly int m_Index;
        private readonly uint m_Generation;

        public SlotHandle(int index, uint generation)
        {
            m_Index = index;
            m_Generation = generation;
        }

        /// <summary>无效句柄（= default）。</summary>
        public static SlotHandle None
        {
            get { return default(SlotHandle); }
        }

        /// <summary>槽位下标。</summary>
        public int Index
        {
            get { return m_Index; }
        }

        /// <summary>代数；有效句柄恒为奇数。</summary>
        public uint Generation
        {
            get { return m_Generation; }
        }

        /// <summary>是否可能有效（奇数代）。是否仍存活要问发放它的 SlotMap。</summary>
        public bool IsValid
        {
            get { return (m_Generation & 1u) != 0u; }
        }

        /// <summary>高 32 位代数、低 32 位下标。</summary>
        public ulong Packed
        {
            get { return ((ulong)m_Generation << 32) | (uint)m_Index; }
        }

        public static SlotHandle FromPacked(ulong packed)
        {
            return new SlotHandle((int)(uint)packed, (uint)(packed >> 32));
        }

        public bool Equals(SlotHandle other)
        {
            return m_Index == other.m_Index && m_Generation == other.m_Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is SlotHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)m_Generation * 397) ^ m_Index;
        }

        public static bool operator ==(SlotHandle left, SlotHandle right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SlotHandle left, SlotHandle right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Utility.Text.Format("SlotHandle({0}:{1})", m_Index, m_Generation);
        }
    }

    /// <summary>
    /// 槽位表：代际句柄 + 稠密存储。插入 / 删除 / 查找 O(1)，值连续存放便于逐个遍历；
    /// 删除后旧句柄的代数失配，查找返回"不存在"，不会误取到复用该槽位的新元素。
    ///
    /// 布局：槽位数组（代数 + 稠密下标，空闲槽串成 LIFO 链表）→ 稠密值数组 + 稠密→槽位反查数组。
    /// 删除把最后一个稠密元素挪进空洞（swap-remove），因此<b>稠密顺序会随删除变化</b>，不要依赖。
    /// 代数用奇偶区分占用（奇）/ 空闲（偶）；某槽位代数用尽（2^31 次复用）后永久退役，不会回绕误配。
    ///
    /// 零分配：数组只在插入超出容量时翻倍；无委托 / LINQ / 装箱，枚举器是 struct（<c>ref</c> 取值）。
    /// 枚举期间插入 / 删除会抛出；要边遍历边删，按稠密下标倒序：
    /// <code>
    /// for (int i = map.Count - 1; i &gt;= 0; i--)
    ///     if (map.ValueAt(i).Dead) map.RemoveAt(i);
    /// </code>
    ///
    /// 线程：非线程安全。
    /// </summary>
    public sealed class SlotMap<T>
    {
        private struct Slot
        {
            public uint Generation;
            // 占用时为稠密下标；空闲时为下一个空闲槽（-1 表示链尾）。
            public int DenseOrNextFree;
        }

        private Slot[] m_Slots;
        private int m_SlotCount;
        private int m_FreeHead;
        private T[] m_Values;
        private int[] m_DenseToSlot;
        private int m_Count;
        private int m_Version;

        public SlotMap(int capacity = 16)
        {
            if (capacity < 4)
            {
                capacity = 4;
            }

            m_Slots = new Slot[capacity];
            m_Values = new T[capacity];
            m_DenseToSlot = new int[capacity];
            m_FreeHead = -1;
        }

        /// <summary>元素数量（= 稠密区长度）。</summary>
        public int Count
        {
            get { return m_Count; }
        }

        /// <summary>当前容量（稠密区与槽位表同步增长）。</summary>
        public int Capacity
        {
            get { return m_Values.Length; }
        }

        /// <summary>按句柄取值引用；句柄失效时抛出。</summary>
        public ref T this[SlotHandle handle]
        {
            get
            {
                int dense = DenseIndexOf(handle);
                if (dense < 0)
                {
                    throw new FrameworkException(Utility.Text.Format(
                        "SlotMap：句柄 {0} 已失效（元素已移除或句柄来自别的 SlotMap）。先用 Contains / TryGetValue 判断。", handle));
                }

                return ref m_Values[dense];
            }
        }

        /// <summary>插入并返回句柄。</summary>
        public SlotHandle Add(T value)
        {
            if (m_Count == m_Values.Length)
            {
                Grow(m_Values.Length * 2);
            }

            int slotIndex;
            if (m_FreeHead >= 0)
            {
                slotIndex = m_FreeHead;
                m_FreeHead = m_Slots[slotIndex].DenseOrNextFree;
            }
            else
            {
                // 只有存在退役槽位时槽位表才会比稠密区先满。
                if (m_SlotCount == m_Slots.Length)
                {
                    Array.Resize(ref m_Slots, m_Slots.Length * 2);
                }

                slotIndex = m_SlotCount++;
            }

            int dense = m_Count++;
            ref Slot slot = ref m_Slots[slotIndex];
            slot.Generation++;              // 偶 → 奇：占用
            slot.DenseOrNextFree = dense;
            m_Values[dense] = value;
            m_DenseToSlot[dense] = slotIndex;
            m_Version++;
            return new SlotHandle(slotIndex, slot.Generation);
        }

        /// <summary>句柄是否仍指向存活元素。</summary>
        public bool Contains(SlotHandle handle)
        {
            return DenseIndexOf(handle) >= 0;
        }

        public bool TryGetValue(SlotHandle handle, out T value)
        {
            int dense = DenseIndexOf(handle);
            if (dense < 0)
            {
                value = default(T);
                return false;
            }

            value = m_Values[dense];
            return true;
        }

        /// <summary>句柄对应的稠密下标；失效返回 -1。</summary>
        public int DenseIndexOf(SlotHandle handle)
        {
            int index = handle.Index;
            if ((uint)index >= (uint)m_SlotCount)
            {
                return -1;
            }

            Slot slot = m_Slots[index];
            if (slot.Generation != handle.Generation || (slot.Generation & 1u) == 0u)
            {
                return -1;
            }

            return slot.DenseOrNextFree;
        }

        /// <summary>移除；句柄失效时返回 false。</summary>
        public bool Remove(SlotHandle handle)
        {
            int dense = DenseIndexOf(handle);
            if (dense < 0)
            {
                return false;
            }

            RemoveAt(dense);
            return true;
        }

        /// <summary>移除并取出值；句柄失效时返回 false。</summary>
        public bool Remove(SlotHandle handle, out T value)
        {
            int dense = DenseIndexOf(handle);
            if (dense < 0)
            {
                value = default(T);
                return false;
            }

            value = m_Values[dense];
            RemoveAt(dense);
            return true;
        }

        /// <summary>按稠密下标移除（最后一个稠密元素会被挪到该位置）。</summary>
        public void RemoveAt(int denseIndex)
        {
            ThrowIfDenseOutOfRange(denseIndex);
            int slotIndex = m_DenseToSlot[denseIndex];
            int last = m_Count - 1;
            if (denseIndex != last)
            {
                int movedSlot = m_DenseToSlot[last];
                m_Values[denseIndex] = m_Values[last];
                m_DenseToSlot[denseIndex] = movedSlot;
                m_Slots[movedSlot].DenseOrNextFree = denseIndex;
            }

            m_Values[last] = default(T);   // 不延长引用寿命
            m_Count = last;
            ReleaseSlot(slotIndex);
            m_Version++;
        }

        /// <summary>按稠密下标取值引用（0 ≤ i &lt; Count）。</summary>
        public ref T ValueAt(int denseIndex)
        {
            ThrowIfDenseOutOfRange(denseIndex);
            return ref m_Values[denseIndex];
        }

        /// <summary>按稠密下标取句柄。</summary>
        public SlotHandle HandleAt(int denseIndex)
        {
            ThrowIfDenseOutOfRange(denseIndex);
            int slotIndex = m_DenseToSlot[denseIndex];
            return new SlotHandle(slotIndex, m_Slots[slotIndex].Generation);
        }

        /// <summary>稠密值区（长度 = Count）。插入 / 删除后失效，不要跨帧持有。</summary>
        public Span<T> AsSpan()
        {
            return new Span<T>(m_Values, 0, m_Count);
        }

        /// <summary>全部移除（所有旧句柄失效），容量保留。</summary>
        public void Clear()
        {
            for (int dense = m_Count - 1; dense >= 0; dense--)
            {
                ReleaseSlot(m_DenseToSlot[dense]);
            }

            Array.Clear(m_Values, 0, m_Count);
            m_Count = 0;
            m_Version++;
        }

        /// <summary>确保容量不小于 <paramref name="capacity"/>。</summary>
        public void EnsureCapacity(int capacity)
        {
            if (capacity > m_Values.Length)
            {
                Grow(capacity);
            }
        }

        /// <summary>按稠密顺序枚举值（<c>foreach (ref var v in map)</c> 可原地修改）；枚举期间插入 / 删除会抛出。</summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        /// <summary>测试用：把槽位代数直接设为指定值，以便覆盖代数用尽的退役路径。</summary>
        internal void SetGenerationForTest(int slotIndex, uint generation)
        {
            m_Slots[slotIndex].Generation = generation;
        }

        private void ReleaseSlot(int slotIndex)
        {
            ref Slot slot = ref m_Slots[slotIndex];
            slot.Generation++;              // 奇 → 偶：空闲
            if (slot.Generation == 0u)
            {
                // uint 用尽后回绕到 0：该槽位退役，不再进空闲链，避免旧句柄与新代数碰撞。
                slot.DenseOrNextFree = -1;
                return;
            }

            slot.DenseOrNextFree = m_FreeHead;
            m_FreeHead = slotIndex;
        }

        private void Grow(int capacity)
        {
            Array.Resize(ref m_Values, capacity);
            Array.Resize(ref m_DenseToSlot, capacity);
            // 槽位表可能因退役槽位多于稠密区，只增不减。
            if (m_Slots.Length < capacity)
            {
                Array.Resize(ref m_Slots, capacity);
            }

            m_Version++;
        }

        private void ThrowIfDenseOutOfRange(int denseIndex)
        {
            if ((uint)denseIndex >= (uint)m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("SlotMap：稠密下标 {0} 越界，Count={1}。", denseIndex, m_Count));
            }
        }

        /// <summary>按稠密顺序的 struct 枚举器。</summary>
        public struct Enumerator
        {
            private readonly SlotMap<T> m_Map;
            private readonly int m_Version;
            private int m_Index;

            internal Enumerator(SlotMap<T> map)
            {
                m_Map = map;
                m_Version = map.m_Version;
                m_Index = -1;
            }

            public ref T Current
            {
                get { return ref m_Map.m_Values[m_Index]; }
            }

            /// <summary>当前元素的句柄。</summary>
            public SlotHandle CurrentHandle
            {
                get
                {
                    int slotIndex = m_Map.m_DenseToSlot[m_Index];
                    return new SlotHandle(slotIndex, m_Map.m_Slots[slotIndex].Generation);
                }
            }

            public bool MoveNext()
            {
                if (m_Version != m_Map.m_Version)
                {
                    throw new FrameworkException("SlotMap：枚举期间集合被修改。");
                }

                return ++m_Index < m_Map.m_Count;
            }
        }
    }
}
