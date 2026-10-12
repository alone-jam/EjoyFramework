//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 双端队列：环形数组实现，两端 O(1) 压入 / 弹出，O(1) 按下标访问。零分配：底层数组只增不减
    /// （<see cref="TrimExcess"/> 显式收缩），无委托 / LINQ / 装箱，枚举器是 struct。
    ///
    /// 用途：BFS / 工作窃取队列、撤销栈（两端都要动）、滑动窗口最值（单调队列）。
    /// 只需固定窗口或按序号寻址时用 <see cref="RingBuffer{T}"/>。
    ///
    /// 线程：非线程安全，与 <c>List&lt;T&gt;</c> 相同。
    /// </summary>
    public sealed class Deque<T>
    {
        private T[] m_Items;
        private int m_Head;
        private int m_Count;
        private int m_Version;

        public Deque(int capacity = 16)
        {
            m_Items = new T[capacity < 4 ? 4 : capacity];
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

        /// <summary>按下标访问（0 = 队首）。返回引用，可原地修改大结构体。</summary>
        public ref T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_Count)
                {
                    throw new FrameworkException(Utility.Text.Format("Deque：下标 {0} 越界，Count={1}。", index, m_Count));
                }

                return ref m_Items[PhysicalIndex(index)];
            }
        }

        /// <summary>压入队尾。</summary>
        public void PushBack(T item)
        {
            if (m_Count == m_Items.Length)
            {
                Resize(m_Items.Length * 2);
            }

            m_Items[PhysicalIndex(m_Count)] = item;
            m_Count++;
            m_Version++;
        }

        /// <summary>压入队首。</summary>
        public void PushFront(T item)
        {
            if (m_Count == m_Items.Length)
            {
                Resize(m_Items.Length * 2);
            }

            m_Head = m_Head == 0 ? m_Items.Length - 1 : m_Head - 1;
            m_Items[m_Head] = item;
            m_Count++;
            m_Version++;
        }

        /// <summary>弹出队首；空时抛出。</summary>
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

        /// <summary>弹出队尾；空时抛出。</summary>
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

        /// <summary>查看队首；空时抛出。</summary>
        public ref T PeekFront()
        {
            ThrowIfEmpty("PeekFront");
            return ref m_Items[m_Head];
        }

        /// <summary>查看队尾；空时抛出。</summary>
        public ref T PeekBack()
        {
            ThrowIfEmpty("PeekBack");
            return ref m_Items[PhysicalIndex(m_Count - 1)];
        }

        /// <summary>清空（引用置默认，容量保留）。</summary>
        public void Clear()
        {
            int first = Math.Min(m_Count, m_Items.Length - m_Head);
            Array.Clear(m_Items, m_Head, first);
            if (first < m_Count)
            {
                Array.Clear(m_Items, 0, m_Count - first);
            }

            m_Head = 0;
            m_Count = 0;
            m_Version++;
        }

        /// <summary>确保容量不小于 <paramref name="capacity"/>。</summary>
        public void EnsureCapacity(int capacity)
        {
            if (capacity > m_Items.Length)
            {
                Resize(capacity);
            }
        }

        /// <summary>把容量收缩到 max(Count, 4)（会分配新数组，不要在热路径调用）。</summary>
        public void TrimExcess()
        {
            int capacity = m_Count < 4 ? 4 : m_Count;
            if (capacity < m_Items.Length)
            {
                Resize(capacity);
            }
        }

        /// <summary>按队首到队尾的顺序复制到 <paramref name="destination"/>，返回复制个数；目标不足时抛出。</summary>
        public int CopyTo(Span<T> destination)
        {
            if (destination.Length < m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("Deque.CopyTo：目标长度 {0} 小于 Count={1}。", destination.Length, m_Count));
            }

            int first = Math.Min(m_Count, m_Items.Length - m_Head);
            new ReadOnlySpan<T>(m_Items, m_Head, first).CopyTo(destination);
            if (first < m_Count)
            {
                new ReadOnlySpan<T>(m_Items, 0, m_Count - first).CopyTo(destination.Slice(first));
            }

            return m_Count;
        }

        /// <summary>队首到队尾枚举；枚举期间压入 / 弹出会抛出（原地修改元素内容不算）。</summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        private int PhysicalIndex(int logicalIndex)
        {
            return PhysicalIndex(m_Head, logicalIndex, m_Items.Length);
        }

        // head / logicalIndex 均在 [0, capacity) 内；先判断回绕，避免大容量下相加溢出。
        internal static int PhysicalIndex(int head, int logicalIndex, int capacity)
        {
            int remaining = capacity - head;
            return logicalIndex >= remaining ? logicalIndex - remaining : head + logicalIndex;
        }

        private void RemoveFront()
        {
            m_Items[m_Head] = default(T);   // 不延长引用寿命
            m_Head = m_Head + 1 == m_Items.Length ? 0 : m_Head + 1;
            m_Count--;
            m_Version++;
        }

        private void RemoveBack(int physicalIndex)
        {
            m_Items[physicalIndex] = default(T);
            m_Count--;
            m_Version++;
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
                throw new FrameworkException(Utility.Text.Format("Deque.{0}：队列为空。", apiName));
            }
        }

        /// <summary>队首到队尾的 struct 枚举器。</summary>
        public struct Enumerator
        {
            private readonly Deque<T> m_Deque;
            private readonly int m_Version;
            private int m_Index;

            internal Enumerator(Deque<T> deque)
            {
                m_Deque = deque;
                m_Version = deque.m_Version;
                m_Index = -1;
            }

            public ref T Current
            {
                get { return ref m_Deque.m_Items[m_Deque.PhysicalIndex(m_Index)]; }
            }

            public bool MoveNext()
            {
                if (m_Version != m_Deque.m_Version)
                {
                    throw new FrameworkException("Deque：枚举期间集合被修改。");
                }

                return ++m_Index < m_Deque.m_Count;
            }
        }
    }
}
