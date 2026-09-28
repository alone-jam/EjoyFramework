//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core
{
    /// <summary>
    /// <see cref="RingBuffer{T}"/> 写满后的处理方式。
    /// </summary>
    public enum RingBufferOverflow : byte
    {
        /// <summary>容量翻倍（唯一会分配的路径，稳态后不再发生）。</summary>
        Grow = 0,

        /// <summary>挤掉最旧元素（固定窗口：日志尾巴、帧时间采样、输入 / 快照历史）。</summary>
        Overwrite = 1,

        /// <summary>拒绝写入：<see cref="RingBuffer{T}.TryPush"/> 返回 false，<see cref="RingBuffer{T}.Push"/> 抛出。</summary>
        Reject = 2,
    }

    /// <summary>
    /// 环形缓冲（FIFO），带单调序号寻址。零分配：底层数组只在 <see cref="RingBufferOverflow.Grow"/> 写满时扩容，
    /// 无委托 / LINQ / 装箱，枚举器是 struct。
    ///
    /// 序号：每个写入的元素得到一个序号，等于上一个元素的序号 + 1（int 回绕安全，比较一律用差值）。
    /// 最旧元素的序号是 <see cref="HeadSequence"/>，下一个写入的元素得到 <see cref="NextSequence"/>。
    /// 用 <see cref="Clear(int)"/> 把序号对齐到外部编号（如网络 tick），之后逐 tick 写入，即可按 tick 直接取历史：
    /// <code>
    /// history.Clear(firstTick);
    /// history.PushRef() = snapshot;                     // 每 tick 一次
    /// if (history.TryGetAtSequence(ackTick, out var s)) Reconcile(s);
    /// history.DiscardBefore(ackTick);                   // 丢弃已确认的
    /// history.DiscardFrom(rollbackTick);                // 回滚：丢弃预测的
    /// </code>
    /// 序号是连续的：这里不存放空洞。乱序到达的稀疏序号请由调用方先排序或自行映射。
    ///
    /// 线程：非线程安全，与 <c>List&lt;T&gt;</c> 相同。
    /// </summary>
    public sealed class RingBuffer<T>
    {
        private T[] m_Items;
        private int m_Head;
        private int m_Count;
        private int m_HeadSequence;
        private int m_Version;
        private readonly RingBufferOverflow m_Overflow;

        public RingBuffer(int capacity, RingBufferOverflow overflow = RingBufferOverflow.Grow)
        {
            if (capacity < 1)
            {
                throw new FrameworkException(Utility.Text.Format("RingBuffer：容量必须 ≥ 1，实际 {0}。", capacity));
            }

            m_Items = new T[capacity];
            m_Overflow = overflow;
        }

        /// <summary>元素数量。</summary>
        public int Count
        {
            get { return m_Count; }
        }

        /// <summary>当前容量。</summary>
        public int Capacity
        {
            get { return m_Items.Length; }
        }

        /// <summary>写满后的处理方式。</summary>
        public RingBufferOverflow Overflow
        {
            get { return m_Overflow; }
        }

        public bool IsEmpty
        {
            get { return m_Count == 0; }
        }

        public bool IsFull
        {
            get { return m_Count == m_Items.Length; }
        }

        /// <summary>最旧元素的序号；为空时等于 <see cref="NextSequence"/>。</summary>
        public int HeadSequence
        {
            get { return m_HeadSequence; }
        }

        /// <summary>下一个写入的元素将得到的序号（= 最新元素序号 + 1）。</summary>
        public int NextSequence
        {
            get { return unchecked(m_HeadSequence + m_Count); }
        }

        /// <summary>按逻辑下标访问（0 = 最旧）。返回引用，可原地修改大结构体。</summary>
        public ref T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_Count)
                {
                    throw new FrameworkException(Utility.Text.Format("RingBuffer：下标 {0} 越界，Count={1}。", index, m_Count));
                }

                return ref m_Items[PhysicalIndex(index)];
            }
        }

        /// <summary>
        /// 写入队尾。仅在 <see cref="RingBufferOverflow.Reject"/> 且已满时返回 false；
        /// <see cref="RingBufferOverflow.Overwrite"/> 已满时挤掉最旧元素（<see cref="HeadSequence"/> 前进 1）。
        /// </summary>
        public bool TryPush(T item)
        {
            if (m_Count == m_Items.Length && !MakeRoomForPush())
            {
                return false;
            }

            m_Items[PhysicalIndex(m_Count)] = item;
            m_Count++;
            m_Version++;
            return true;
        }

        /// <summary>写入队尾；<see cref="RingBufferOverflow.Reject"/> 且已满时抛出。</summary>
        public void Push(T item)
        {
            if (!TryPush(item))
            {
                throw new FrameworkException(Utility.Text.Format(
                    "RingBuffer：已满（Capacity={0}，策略 Reject）。先 PopFront / DiscardBefore 腾出空间，或改用 TryPush 处理拒绝。", m_Items.Length));
            }
        }

        /// <summary>
        /// 在队尾占一个槽位并返回其引用（已清为 default），大结构体直接原地写入、免一次复制。
        /// 满时的行为同 <see cref="Push"/>。
        /// </summary>
        public ref T PushRef()
        {
            if (m_Count == m_Items.Length && !MakeRoomForPush())
            {
                throw new FrameworkException(Utility.Text.Format(
                    "RingBuffer：已满（Capacity={0}，策略 Reject）。先 PopFront / DiscardBefore 腾出空间。", m_Items.Length));
            }

            int index = PhysicalIndex(m_Count);
            m_Count++;
            m_Version++;
            m_Items[index] = default(T);
            return ref m_Items[index];
        }

        /// <summary>查看最旧元素；空时抛出。</summary>
        public ref T PeekFront()
        {
            ThrowIfEmpty("PeekFront");
            return ref m_Items[m_Head];
        }

        /// <summary>查看最新元素；空时抛出。</summary>
        public ref T PeekBack()
        {
            ThrowIfEmpty("PeekBack");
            return ref m_Items[PhysicalIndex(m_Count - 1)];
        }

        /// <summary>取出最旧元素；空时抛出。</summary>
        public T PopFront()
        {
            ThrowIfEmpty("PopFront");
            T item = m_Items[m_Head];
            RemoveFront();
            return item;
        }

        public bool TryPopFront(out T item)
        {
            if (m_Count == 0)
            {
                item = default(T);
                return false;
            }

            item = m_Items[m_Head];
            RemoveFront();
            return true;
        }

        /// <summary>取出最新元素（回滚用）；空时抛出。</summary>
        public T PopBack()
        {
            ThrowIfEmpty("PopBack");
            int index = PhysicalIndex(m_Count - 1);
            T item = m_Items[index];
            RemoveBack(index);
            return item;
        }

        public bool TryPopBack(out T item)
        {
            if (m_Count == 0)
            {
                item = default(T);
                return false;
            }

            int index = PhysicalIndex(m_Count - 1);
            item = m_Items[index];
            RemoveBack(index);
            return true;
        }

        /// <summary>序号是否在缓冲内（[HeadSequence, NextSequence)）。</summary>
        public bool ContainsSequence(int sequence)
        {
            return (uint)unchecked(sequence - m_HeadSequence) < (uint)m_Count;
        }

        /// <summary>按序号取元素引用；不在缓冲内时抛出。</summary>
        public ref T AtSequence(int sequence)
        {
            int offset = unchecked(sequence - m_HeadSequence);
            if ((uint)offset >= (uint)m_Count)
            {
                throw new FrameworkException(Utility.Text.Format(
                    "RingBuffer：序号 {0} 不在 [{1}, {2}) 内。先用 ContainsSequence / TryGetAtSequence 判断。",
                    sequence, m_HeadSequence, NextSequence));
            }

            return ref m_Items[PhysicalIndex(offset)];
        }

        public bool TryGetAtSequence(int sequence, out T item)
        {
            int offset = unchecked(sequence - m_HeadSequence);
            if ((uint)offset >= (uint)m_Count)
            {
                item = default(T);
                return false;
            }

            item = m_Items[PhysicalIndex(offset)];
            return true;
        }

        /// <summary>
        /// 丢弃序号早于 <paramref name="sequence"/> 的元素（如已被服务器确认的输入），返回丢弃个数。
        /// <paramref name="sequence"/> 超过最新元素时清空，但 <see cref="HeadSequence"/> 停在 <see cref="NextSequence"/>，
        /// 不会跳到 <paramref name="sequence"/>（序号只由写入推进）。
        /// </summary>
        public int DiscardBefore(int sequence)
        {
            int removed = 0;
            while (m_Count > 0 && unchecked(sequence - m_HeadSequence) > 0)
            {
                RemoveFront();
                removed++;
            }

            return removed;
        }

        /// <summary>丢弃序号不早于 <paramref name="sequence"/> 的元素（回滚预测），返回丢弃个数。</summary>
        public int DiscardFrom(int sequence)
        {
            int removed = 0;
            while (m_Count > 0 && unchecked(NextSequence - 1 - sequence) >= 0)
            {
                RemoveBack(PhysicalIndex(m_Count - 1));
                removed++;
            }

            return removed;
        }

        /// <summary>清空，容量保留；序号接着走（<see cref="HeadSequence"/> = 原 <see cref="NextSequence"/>）。</summary>
        public void Clear()
        {
            Clear(NextSequence);
        }

        /// <summary>清空并把下一个写入的序号设为 <paramref name="nextSequence"/>。</summary>
        public void Clear(int nextSequence)
        {
            ClearSlots();
            m_Head = 0;
            m_Count = 0;
            m_HeadSequence = nextSequence;
            m_Version++;
        }

        /// <summary>确保容量不小于 <paramref name="capacity"/>（任何策略下都可显式扩容）。</summary>
        public void EnsureCapacity(int capacity)
        {
            if (capacity > m_Items.Length)
            {
                Resize(capacity);
            }
        }

        /// <summary>按从旧到新的顺序复制到 <paramref name="destination"/>，返回复制个数；目标不足时抛出。</summary>
        public int CopyTo(Span<T> destination)
        {
            if (destination.Length < m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("RingBuffer.CopyTo：目标长度 {0} 小于 Count={1}。", destination.Length, m_Count));
            }

            int first = Math.Min(m_Count, m_Items.Length - m_Head);
            new ReadOnlySpan<T>(m_Items, m_Head, first).CopyTo(destination);
            if (first < m_Count)
            {
                new ReadOnlySpan<T>(m_Items, 0, m_Count - first).CopyTo(destination.Slice(first));
            }

            return m_Count;
        }

        /// <summary>从旧到新枚举；枚举期间写入 / 取出会抛出（原地修改元素内容不算）。</summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        private int PhysicalIndex(int logicalIndex)
        {
            int index = m_Head + logicalIndex;
            return index >= m_Items.Length ? index - m_Items.Length : index;
        }

        // 已满时按策略腾位置；返回 false 表示拒绝。
        private bool MakeRoomForPush()
        {
            switch (m_Overflow)
            {
                case RingBufferOverflow.Grow:
                    Resize(m_Items.Length * 2);
                    return true;
                case RingBufferOverflow.Overwrite:
                    RemoveFront();
                    return true;
                default:
                    return false;
            }
        }

        private void RemoveFront()
        {
            m_Items[m_Head] = default(T);   // 不延长引用寿命
            m_Head = m_Head + 1 == m_Items.Length ? 0 : m_Head + 1;
            m_Count--;
            m_HeadSequence = unchecked(m_HeadSequence + 1);
            m_Version++;
        }

        private void RemoveBack(int physicalIndex)
        {
            m_Items[physicalIndex] = default(T);
            m_Count--;
            m_Version++;
        }

        private void ClearSlots()
        {
            int first = Math.Min(m_Count, m_Items.Length - m_Head);
            Array.Clear(m_Items, m_Head, first);
            if (first < m_Count)
            {
                Array.Clear(m_Items, 0, m_Count - first);
            }
        }

        private void Resize(int capacity)
        {
            T[] items = new T[capacity];
            CopyTo(items);
            m_Items = items;
            m_Head = 0;
            m_Version++;
        }

        private void ThrowIfEmpty(string apiName)
        {
            if (m_Count == 0)
            {
                throw new FrameworkException(Utility.Text.Format("RingBuffer.{0}：缓冲为空。", apiName));
            }
        }

        /// <summary>从旧到新的 struct 枚举器。</summary>
        public struct Enumerator
        {
            private readonly RingBuffer<T> m_Buffer;
            private readonly int m_Version;
            private int m_Index;

            internal Enumerator(RingBuffer<T> buffer)
            {
                m_Buffer = buffer;
                m_Version = buffer.m_Version;
                m_Index = -1;
            }

            public ref T Current
            {
                get { return ref m_Buffer.m_Items[m_Buffer.PhysicalIndex(m_Index)]; }
            }

            public bool MoveNext()
            {
                if (m_Version != m_Buffer.m_Version)
                {
                    throw new FrameworkException("RingBuffer：枚举期间集合被修改。");
                }

                return ++m_Index < m_Buffer.m_Count;
            }
        }
    }
}
