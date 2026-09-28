//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 游戏框架链表。
    /// 特性：
    ///   1) 节点池避免频繁分配 GC 压力（带 MaxCachedNodes 上限防泄漏）。
    ///   2) Enumerator 支持"删除当前节点"安全遍历（缓存 Next 在 MoveNext 之前）。
    ///   3) Remove(Node) 验证节点归属，过期引用 noop 而非抛异常。
    /// 警告：非线程安全。多线程访问需外部加锁，不要依赖 SyncRoot 实现自动同步。
    /// </summary>
    public sealed class GameLinkedList<T> : ICollection<T>, IEnumerable<T>, ICollection, IEnumerable
    {
        /// <summary>
        /// 节点缓存默认上限，避免长生命周期持续累积节点导致内存泄漏。
        /// </summary>
        public const int DefaultMaxCachedNodeCount = 1024;

        private readonly LinkedList<T> m_LinkedList;
        private readonly Queue<LinkedListNode<T>> m_CachedNodes;
        private int m_MaxCachedNodeCount;

        public GameLinkedList()
        {
            m_LinkedList = new LinkedList<T>();
            m_CachedNodes = new Queue<LinkedListNode<T>>();
            m_MaxCachedNodeCount = DefaultMaxCachedNodeCount;
        }

        public int Count
        {
            get { return m_LinkedList.Count; }
        }

        public int CachedNodeCount
        {
            get { return m_CachedNodes.Count; }
        }

        /// <summary>
        /// 节点缓存上限。设置后立即裁剪超出部分。
        /// </summary>
        public int MaxCachedNodeCount
        {
            get { return m_MaxCachedNodeCount; }
            set
            {
                if (value < 0)
                {
                    throw new FrameworkException("MaxCachedNodeCount cannot be negative.");
                }
                m_MaxCachedNodeCount = value;
                while (m_CachedNodes.Count > m_MaxCachedNodeCount)
                {
                    m_CachedNodes.Dequeue();
                }
            }
        }

        public LinkedListNode<T> First
        {
            get { return m_LinkedList.First; }
        }

        public LinkedListNode<T> Last
        {
            get { return m_LinkedList.Last; }
        }

        public bool IsReadOnly
        {
            get { return ((ICollection<T>)m_LinkedList).IsReadOnly; }
        }

        public object SyncRoot
        {
            get { return ((ICollection)m_LinkedList).SyncRoot; }
        }

        public bool IsSynchronized
        {
            get { return ((ICollection)m_LinkedList).IsSynchronized; }
        }

        public LinkedListNode<T> AddAfter(LinkedListNode<T> node, T value)
        {
            LinkedListNode<T> newNode = AcquireNode(value);
            m_LinkedList.AddAfter(node, newNode);
            return newNode;
        }

        public LinkedListNode<T> AddBefore(LinkedListNode<T> node, T value)
        {
            LinkedListNode<T> newNode = AcquireNode(value);
            m_LinkedList.AddBefore(node, newNode);
            return newNode;
        }

        public LinkedListNode<T> AddFirst(T value)
        {
            LinkedListNode<T> node = AcquireNode(value);
            m_LinkedList.AddFirst(node);
            return node;
        }

        public LinkedListNode<T> AddLast(T value)
        {
            LinkedListNode<T> node = AcquireNode(value);
            m_LinkedList.AddLast(node);
            return node;
        }

        /// <summary>
        /// 清空链表，并把节点直接归还节点池。逐节点 RemoveFirst 而非先快照后批量回收，
        /// 避免每次 Clear 都分配一个临时快照数组（GC 压力）。
        /// RemoveFirst 后该节点的 .List 已为 null，可安全 reuse；遍历始终取新的 First，无悬挂 .Next 之虞。
        /// </summary>
        public void Clear()
        {
            LinkedListNode<T> node = m_LinkedList.First;
            while (node != null)
            {
                // 先把节点从 list 中摘下（List 归属归零），再回收——此时它既不在 list 也未被复用前的瞬态可控。
                m_LinkedList.Remove(node);
                ReleaseNode(node);
                node = m_LinkedList.First;
            }
        }

        public void ClearCachedNodes()
        {
            m_CachedNodes.Clear();
        }

        public bool Contains(T value)
        {
            return m_LinkedList.Contains(value);
        }

        public bool Remove(T value)
        {
            LinkedListNode<T> node = m_LinkedList.Find(value);
            if (node != null)
            {
                m_LinkedList.Remove(node);
                ReleaseNode(node);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 移除指定结点。若节点不属于当前链表（已被移除/属于其他链表），返回 false 而非抛异常。
        /// </summary>
        public bool Remove(LinkedListNode<T> node)
        {
            if (node == null) return false;
            if (node.List != m_LinkedList) return false;
            m_LinkedList.Remove(node);
            ReleaseNode(node);
            return true;
        }

        public bool RemoveFirst()
        {
            LinkedListNode<T> first = m_LinkedList.First;
            if (first == null) return false;
            m_LinkedList.RemoveFirst();
            ReleaseNode(first);
            return true;
        }

        public bool RemoveLast()
        {
            LinkedListNode<T> last = m_LinkedList.Last;
            if (last == null) return false;
            m_LinkedList.RemoveLast();
            ReleaseNode(last);
            return true;
        }

        public LinkedListNode<T> Find(T value)
        {
            return m_LinkedList.Find(value);
        }

        public LinkedListNode<T> FindLast(T value)
        {
            return m_LinkedList.FindLast(value);
        }

        public void CopyTo(T[] array, int index)
        {
            m_LinkedList.CopyTo(array, index);
        }

        public void CopyTo(Array array, int index)
        {
            ((ICollection)m_LinkedList).CopyTo(array, index);
        }

        void ICollection<T>.Add(T value)
        {
            AddLast(value);
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(m_LinkedList);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private LinkedListNode<T> AcquireNode(T value)
        {
            LinkedListNode<T> node;
            if (m_CachedNodes.Count > 0)
            {
                node = m_CachedNodes.Dequeue();
                node.Value = value;
            }
            else
            {
                node = new LinkedListNode<T>(value);
            }

            return node;
        }

        private void ReleaseNode(LinkedListNode<T> node)
        {
            node.Value = default(T);
            if (m_CachedNodes.Count < m_MaxCachedNodeCount)
            {
                m_CachedNodes.Enqueue(node);
            }
            // 超出上限直接丢给 GC
        }

        /// <summary>
        /// 安全枚举器。删除当前节点（用户在 handler 中 Remove(Current) ）安全；删除 Next 节点不安全。
        /// </summary>
        public struct Enumerator : IEnumerator<T>, IEnumerator
        {
            private LinkedListNode<T> m_Current;
            private LinkedListNode<T> m_Next;
            private readonly LinkedList<T> m_List;

            internal Enumerator(LinkedList<T> list)
            {
                m_List = list;
                m_Current = null;
                m_Next = list.First;
            }

            public T Current
            {
                get
                {
                    if (m_Current != null)
                    {
                        return m_Current.Value;
                    }

                    throw new FrameworkException("Current is invalid.");
                }
            }

            object IEnumerator.Current
            {
                get { return Current; }
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (m_Next != null)
                {
                    m_Current = m_Next;
                    m_Next = m_Current.Next;
                    return true;
                }

                return false;
            }

            void IEnumerator.Reset()
            {
                m_Current = null;
                m_Next = m_List.First;
            }
        }
    }
}
