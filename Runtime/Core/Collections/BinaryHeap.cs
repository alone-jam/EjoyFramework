//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 二叉堆（最小堆，<paramref name="comparer"/> 判定"更小"者先出）。零分配：底层数组只增不减，
    /// 不用委托（<c>IComparer&lt;T&gt;</c> 单例），无 LINQ/装箱。
    ///
    /// 用途：资源请求队列（优先级 + 序号）、定时器到期堆、AI 效用排序、A* open 表。
    /// 非稳定：相等元素出堆顺序不保证，需要 FIFO 的调用方把序号编进比较键。
    /// </summary>
    public sealed class BinaryHeap<T>
    {
        private T[] m_Items;
        private int m_Count;
        private readonly IComparer<T> m_Comparer;

        public BinaryHeap(IComparer<T> comparer, int capacity = 16)
        {
            if (comparer == null)
            {
                throw new FrameworkException("BinaryHeap：comparer 不能为 null。");
            }

            m_Comparer = comparer;
            m_Items = new T[capacity < 4 ? 4 : capacity];
        }

        /// <summary>元素数量。</summary>
        public int Count
        {
            get { return m_Count; }
        }

        /// <summary>压入。</summary>
        public void Push(T item)
        {
            if (m_Count == m_Items.Length)
            {
                Array.Resize(ref m_Items, m_Items.Length * 2);
            }

            int index = m_Count++;
            m_Items[index] = item;
            SiftUp(index);
        }

        /// <summary>查看堆顶；空堆抛出。</summary>
        public T Peek()
        {
            if (m_Count == 0)
            {
                throw new FrameworkException("BinaryHeap.Peek：堆为空。");
            }

            return m_Items[0];
        }

        /// <summary>弹出堆顶；空堆抛出。</summary>
        public T Pop()
        {
            if (m_Count == 0)
            {
                throw new FrameworkException("BinaryHeap.Pop：堆为空。");
            }

            T top = m_Items[0];
            m_Count--;
            if (m_Count > 0)
            {
                m_Items[0] = m_Items[m_Count];
                SiftDown(0);
            }

            m_Items[m_Count] = default(T);   // 不延长引用寿命
            return top;
        }

        /// <summary>尝试弹出。</summary>
        public bool TryPop(out T item)
        {
            if (m_Count == 0)
            {
                item = default(T);
                return false;
            }

            item = Pop();
            return true;
        }

        /// <summary>清空（引用置默认，容量保留）。</summary>
        public void Clear()
        {
            Array.Clear(m_Items, 0, m_Count);
            m_Count = 0;
        }

        /// <summary>按下标访问（堆序，非排序序），供诊断遍历。</summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= m_Count)
                {
                    throw new FrameworkException(Utility.Text.Format("BinaryHeap：下标 {0} 越界，Count={1}。", index, m_Count));
                }

                return m_Items[index];
            }
        }

        /// <summary>
        /// 通知堆某个元素的键变小了（优先级提升）：从该位置上浮。需要调用方知道元素下标——
        /// 用 <see cref="IndexOf"/>（O(n)）或自行在元素里维护下标。
        /// </summary>
        public void DecreaseKeyAt(int index)
        {
            if (index < 0 || index >= m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("BinaryHeap：下标 {0} 越界，Count={1}。", index, m_Count));
            }

            SiftUp(index);
        }

        /// <summary>键变大（优先级降低）：从该位置下沉。</summary>
        public void IncreaseKeyAt(int index)
        {
            if (index < 0 || index >= m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("BinaryHeap：下标 {0} 越界，Count={1}。", index, m_Count));
            }

            SiftDown(index);
        }

        /// <summary>线性查找元素下标（引用/值相等）；不存在返回 -1。</summary>
        public int IndexOf(T item)
        {
            EqualityComparer<T> eq = EqualityComparer<T>.Default;
            for (int i = 0; i < m_Count; i++)
            {
                if (eq.Equals(m_Items[i], item))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>移除任意位置的元素（O(log n)）。</summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= m_Count)
            {
                throw new FrameworkException(Utility.Text.Format("BinaryHeap：下标 {0} 越界，Count={1}。", index, m_Count));
            }

            m_Count--;
            if (index == m_Count)
            {
                m_Items[m_Count] = default(T);
                return;
            }

            m_Items[index] = m_Items[m_Count];
            m_Items[m_Count] = default(T);
            // 顶上来的元素可能比父小也可能比子大：两个方向都试一次，只有一个会真正移动。
            SiftUp(index);
            SiftDown(index);
        }

        private void SiftUp(int index)
        {
            T item = m_Items[index];
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (m_Comparer.Compare(item, m_Items[parent]) >= 0)
                {
                    break;
                }

                m_Items[index] = m_Items[parent];
                index = parent;
            }

            m_Items[index] = item;
        }

        private void SiftDown(int index)
        {
            T item = m_Items[index];
            while (true)
            {
                int child = index * 2 + 1;
                if (child >= m_Count)
                {
                    break;
                }

                int right = child + 1;
                if (right < m_Count && m_Comparer.Compare(m_Items[right], m_Items[child]) < 0)
                {
                    child = right;
                }

                if (m_Comparer.Compare(m_Items[child], item) >= 0)
                {
                    break;
                }

                m_Items[index] = m_Items[child];
                index = child;
            }

            m_Items[index] = item;
        }
    }
}
