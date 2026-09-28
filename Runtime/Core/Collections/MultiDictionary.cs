//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 游戏框架多值字典。
    /// 修复：
    ///   - 用 EqualityComparer{TValue}.Default.Equals 替代 TValue.Equals，避免 null 引用 NRE。
    /// 警告：非线程安全。
    /// </summary>
    public sealed class MultiDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, GameLinkedListRange<TValue>>>, IEnumerable
    {
        private static readonly EqualityComparer<TValue> ValueComparer = EqualityComparer<TValue>.Default;

        private readonly GameLinkedList<TValue> m_LinkedList;
        private readonly Dictionary<TKey, GameLinkedListRange<TValue>> m_Dictionary;

        public MultiDictionary()
        {
            m_LinkedList = new GameLinkedList<TValue>();
            m_Dictionary = new Dictionary<TKey, GameLinkedListRange<TValue>>();
        }

        public int Count
        {
            get { return m_Dictionary.Count; }
        }

        public GameLinkedListRange<TValue> this[TKey key]
        {
            get
            {
                GameLinkedListRange<TValue> range = default(GameLinkedListRange<TValue>);
                m_Dictionary.TryGetValue(key, out range);
                return range;
            }
        }

        public void Clear()
        {
            m_Dictionary.Clear();
            m_LinkedList.Clear();
        }

        public bool Contains(TKey key)
        {
            return m_Dictionary.ContainsKey(key);
        }

        public bool Contains(TKey key, TValue value)
        {
            GameLinkedListRange<TValue> range = default(GameLinkedListRange<TValue>);
            if (m_Dictionary.TryGetValue(key, out range))
            {
                return range.Contains(value);
            }

            return false;
        }

        public bool TryGetValue(TKey key, out GameLinkedListRange<TValue> range)
        {
            return m_Dictionary.TryGetValue(key, out range);
        }

        public void Add(TKey key, TValue value)
        {
            GameLinkedListRange<TValue> range = default(GameLinkedListRange<TValue>);
            if (m_Dictionary.TryGetValue(key, out range))
            {
                m_LinkedList.AddBefore(range.Terminal, value);
            }
            else
            {
                LinkedListNode<TValue> first = m_LinkedList.AddLast(value);
                LinkedListNode<TValue> terminal = m_LinkedList.AddLast(default(TValue));
                m_Dictionary.Add(key, new GameLinkedListRange<TValue>(first, terminal));
            }
        }

        public bool Remove(TKey key, TValue value)
        {
            GameLinkedListRange<TValue> range = default(GameLinkedListRange<TValue>);
            if (m_Dictionary.TryGetValue(key, out range))
            {
                for (LinkedListNode<TValue> current = range.First; current != null && current != range.Terminal; current = current.Next)
                {
                    if (ValueComparer.Equals(current.Value, value))
                    {
                        LinkedListNode<TValue> terminal = range.Terminal;
                        bool emptied;

                        if (current == range.First)
                        {
                            LinkedListNode<TValue> next = current.Next;
                            // 区间在移除 First 后是否变空：next 直接指向终结哨兵。
                            emptied = next == terminal;
                            if (!emptied)
                            {
                                // 仍有后续值：区间起点前移到 next（key 条目保留）。
                                m_Dictionary[key] = new GameLinkedListRange<TValue>(next, terminal);
                            }
                        }
                        else
                        {
                            // 非 First 分支：被移除的是中间/末尾的真实值，区间起点 First 仍是真实值，
                            // 故区间不会清空（清空只可能发生在移除 First 且 next==terminal，已落入上面的分支）。
                            // 此前这里写 emptied = range.First == terminal 是恒为 false 的死比较，直接置 false。
                            emptied = false;
                        }

                        m_LinkedList.Remove(current);

                        if (emptied)
                        {
                            // 区间已无真实值：回收终结哨兵并删除 key 条目，避免每个被清空的 key 泄漏一个哨兵节点。
                            m_LinkedList.Remove(terminal);
                            m_Dictionary.Remove(key);
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        public bool RemoveAll(TKey key)
        {
            GameLinkedListRange<TValue> range = default(GameLinkedListRange<TValue>);
            if (m_Dictionary.TryGetValue(key, out range))
            {
                m_Dictionary.Remove(key);

                LinkedListNode<TValue> current = range.First;
                while (current != null)
                {
                    LinkedListNode<TValue> next = current != range.Terminal ? current.Next : null;
                    m_LinkedList.Remove(current);
                    current = next;
                }

                return true;
            }

            return false;
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(m_Dictionary);
        }

        IEnumerator<KeyValuePair<TKey, GameLinkedListRange<TValue>>> IEnumerable<KeyValuePair<TKey, GameLinkedListRange<TValue>>>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, GameLinkedListRange<TValue>>>, IEnumerator
        {
            private Dictionary<TKey, GameLinkedListRange<TValue>>.Enumerator m_Enumerator;

            internal Enumerator(Dictionary<TKey, GameLinkedListRange<TValue>> dictionary)
            {
                m_Enumerator = dictionary.GetEnumerator();
            }

            public KeyValuePair<TKey, GameLinkedListRange<TValue>> Current
            {
                get { return m_Enumerator.Current; }
            }

            object IEnumerator.Current
            {
                get { return m_Enumerator.Current; }
            }

            public void Dispose()
            {
                m_Enumerator.Dispose();
            }

            public bool MoveNext()
            {
                return m_Enumerator.MoveNext();
            }

            void IEnumerator.Reset()
            {
                ((IEnumerator)m_Enumerator).Reset();
            }
        }
    }

    /// <summary>
    /// 链表区间。
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    public struct GameLinkedListRange<T> : IEnumerable<T>, IEnumerable
    {
        private static readonly EqualityComparer<T> ValueComparer = EqualityComparer<T>.Default;

        private readonly LinkedListNode<T> m_First;
        private readonly LinkedListNode<T> m_Terminal;

        public GameLinkedListRange(LinkedListNode<T> first, LinkedListNode<T> terminal)
        {
            if (first == null || terminal == null || first == terminal)
            {
                throw new FrameworkException("Range is invalid.");
            }

            m_First = first;
            m_Terminal = terminal;
        }

        public bool IsValid
        {
            get { return m_First != null && m_Terminal != null && m_First != m_Terminal; }
        }

        public LinkedListNode<T> First
        {
            get { return m_First; }
        }

        public LinkedListNode<T> Terminal
        {
            get { return m_Terminal; }
        }

        public int Count
        {
            get
            {
                if (!IsValid) return 0;
                int count = 0;
                for (LinkedListNode<T> current = m_First; current != null && current != m_Terminal; current = current.Next)
                {
                    count++;
                }
                return count;
            }
        }

        public bool Contains(T value)
        {
            for (LinkedListNode<T> current = m_First; current != null && current != m_Terminal; current = current.Next)
            {
                if (ValueComparer.Equals(current.Value, value))
                {
                    return true;
                }
            }
            return false;
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public struct Enumerator : IEnumerator<T>, IEnumerator
        {
            private readonly GameLinkedListRange<T> m_Range;
            private LinkedListNode<T> m_Current;
            private T m_CurrentValue;

            internal Enumerator(GameLinkedListRange<T> range)
            {
                if (!range.IsValid)
                {
                    throw new FrameworkException("Range is invalid.");
                }

                m_Range = range;
                m_Current = m_Range.m_First;
                m_CurrentValue = default(T);
            }

            public T Current
            {
                get { return m_CurrentValue; }
            }

            object IEnumerator.Current
            {
                get { return m_CurrentValue; }
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (m_Current != null && m_Current != m_Range.m_Terminal)
                {
                    m_CurrentValue = m_Current.Value;
                    m_Current = m_Current.Next;
                    return true;
                }

                return false;
            }

            void IEnumerator.Reset()
            {
                m_Current = m_Range.m_First;
                m_CurrentValue = default(T);
            }
        }
    }
}
