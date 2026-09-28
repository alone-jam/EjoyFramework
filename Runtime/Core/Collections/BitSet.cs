//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 位集（按 64 位字存储）。两种模式：
    ///   • 固定长度（默认）：越过 <see cref="Length"/> 的 <see cref="Set(int)"/> 抛出，适合已知上限的标记表（槽位占用、脏标记）。
    ///   • 可增长（<c>growable: true</c>）：<see cref="Set(int)"/> 越界时自动扩容（字数组翻倍，稳态零分配），适合按 id 标记。
    /// 两种模式下越界的 <see cref="Test"/> 都返回 false、越界的 <see cref="Clear(int)"/> 都是空操作；负下标一律抛出。
    ///
    /// 零分配：<see cref="Count"/> 增量维护（O(1)）；首个置位 / 下一个置位 / 首个空位靠 de Bruijn 位扫描逐字跳过；
    /// 枚举器是 struct，逐个产出置位下标。批量运算（And / Or / Xor / AndNot / Intersects / IsSupersetOf）逐字进行，供掩码匹配。
    ///
    /// 枚举期间修改会抛出；需要边遍历边改时用
    /// <c>for (int i = bits.FirstSet(); i &gt;= 0; i = bits.NextSet(i + 1))</c>。
    ///
    /// 线程：非线程安全。
    /// </summary>
    public sealed class BitSet
    {
        /// <summary>位长度上限：保证换算成 64 位字数时不溢出。</summary>
        public const int MaxLength = int.MaxValue - 63;

        private ulong[] m_Words;
        private int m_Length;
        private int m_Count;
        private int m_Version;
        private readonly bool m_Growable;

        public BitSet(int length, bool growable = false)
        {
            if (length < 0)
            {
                throw new FrameworkException(Utility.Text.Format("BitSet：长度不能为负数，实际 {0}。", length));
            }

            ThrowIfTooLong(length);
            m_Words = new ulong[WordCount(length)];
            m_Length = length;
            m_Growable = growable;
        }

        /// <summary>位长度：[0, Length) 可寻址。可增长模式下随 <see cref="Set(int)"/> 变长。</summary>
        public int Length
        {
            get { return m_Length; }
        }

        /// <summary>置位数（O(1)）。</summary>
        public int Count
        {
            get { return m_Count; }
        }

        public bool IsGrowable
        {
            get { return m_Growable; }
        }

        public bool IsEmpty
        {
            get { return m_Count == 0; }
        }

        public bool this[int index]
        {
            get { return Test(index); }
            set { Set(index, value); }
        }

        /// <summary>测试某位；越过 <see cref="Length"/> 返回 false。</summary>
        public bool Test(int index)
        {
            if ((uint)index >= (uint)m_Length)
            {
                ThrowIfNegative(index);
                return false;
            }

            return (m_Words[index >> 6] & (1UL << (index & 63))) != 0UL;
        }

        /// <summary>置位；返回该位是否由 0 变 1。固定长度下越界抛出，可增长模式下自动扩容。</summary>
        public bool Set(int index)
        {
            if ((uint)index >= (uint)m_Length)
            {
                ThrowIfNegative(index);
                if (!m_Growable)
                {
                    throw new FrameworkException(Utility.Text.Format(
                        "BitSet.Set：下标 {0} 越过固定长度 {1}。构造时给足长度、先 EnsureLength，或用 growable: true。", index, m_Length));
                }

                ThrowIfTooLong(index + 1L);
                EnsureLength(index + 1);
            }

            ulong mask = 1UL << (index & 63);
            ulong word = m_Words[index >> 6];
            if ((word & mask) != 0UL)
            {
                return false;
            }

            m_Words[index >> 6] = word | mask;
            m_Count++;
            m_Version++;
            return true;
        }

        /// <summary>清位；返回该位是否由 1 变 0。越过 <see cref="Length"/> 是空操作。</summary>
        public bool Clear(int index)
        {
            if ((uint)index >= (uint)m_Length)
            {
                ThrowIfNegative(index);
                return false;
            }

            ulong mask = 1UL << (index & 63);
            ulong word = m_Words[index >> 6];
            if ((word & mask) == 0UL)
            {
                return false;
            }

            m_Words[index >> 6] = word & ~mask;
            m_Count--;
            m_Version++;
            return true;
        }

        public void Set(int index, bool value)
        {
            if (value)
            {
                Set(index);
            }
            else
            {
                Clear(index);
            }
        }

        /// <summary>把 [start, start+count) 全部置位；越界规则同 <see cref="Set(int)"/>。</summary>
        public void SetRange(int start, int count)
        {
            ValidateRange(start, count);
            if (count == 0)
            {
                return;
            }

            if (start + count > m_Length)
            {
                if (!m_Growable)
                {
                    throw new FrameworkException(Utility.Text.Format(
                        "BitSet.SetRange：区间 [{0}, {1}) 越过固定长度 {2}。", start, start + count, m_Length));
                }

                EnsureLength(start + count);
            }

            ApplyRange(start, start + count, true);
        }

        /// <summary>把 [start, start+count) 全部清位；越过 <see cref="Length"/> 的部分忽略。</summary>
        public void ClearRange(int start, int count)
        {
            ValidateRange(start, count);
            int end = Math.Min(start + count, m_Length);
            if (end > start)
            {
                ApplyRange(start, end, false);
            }
        }

        /// <summary>把 [0, Length) 全部置位。</summary>
        public void SetAll()
        {
            if (m_Length > 0)
            {
                ApplyRange(0, m_Length, true);
            }
        }

        /// <summary>全部清位（长度与容量保留）。</summary>
        public void ClearAll()
        {
            Array.Clear(m_Words, 0, WordCount(m_Length));
            m_Count = 0;
            m_Version++;
        }

        /// <summary>确保 <see cref="Length"/> 不小于 <paramref name="length"/>；新增位为 0。两种模式都可显式调用。</summary>
        public void EnsureLength(int length)
        {
            if (length <= m_Length)
            {
                return;
            }

            ThrowIfTooLong(length);
            int needed = WordCount(length);
            if (needed > m_Words.Length)
            {
                int words = m_Words.Length * 2;
                Array.Resize(ref m_Words, words > needed ? words : needed);
            }

            m_Length = length;
            m_Version++;
        }

        /// <summary>最低的置位下标；没有置位时返回 -1。</summary>
        public int FirstSet()
        {
            return NextSet(0);
        }

        /// <summary>不小于 <paramref name="from"/> 的最低置位下标；没有时返回 -1。</summary>
        public int NextSet(int from)
        {
            ThrowIfNegative(from);
            if (from >= m_Length)
            {
                return -1;
            }

            int used = WordCount(m_Length);
            int w = from >> 6;
            ulong word = m_Words[w] & (~0UL << (from & 63));
            while (true)
            {
                if (word != 0UL)
                {
                    return (w << 6) + BitMath.TrailingZeroCount(word);
                }

                if (++w >= used)
                {
                    return -1;
                }

                word = m_Words[w];
            }
        }

        /// <summary>最低的空位下标（&lt; Length）；全满时返回 -1（可增长模式下可直接用 <see cref="Length"/>）。</summary>
        public int FirstClear()
        {
            return NextClear(0);
        }

        /// <summary>不小于 <paramref name="from"/> 的最低空位下标（&lt; Length）；没有时返回 -1。</summary>
        public int NextClear(int from)
        {
            ThrowIfNegative(from);
            if (from >= m_Length)
            {
                return -1;
            }

            int used = WordCount(m_Length);
            int w = from >> 6;
            ulong word = ~m_Words[w] & (~0UL << (from & 63));
            while (true)
            {
                if (word != 0UL)
                {
                    int index = (w << 6) + BitMath.TrailingZeroCount(word);
                    return index < m_Length ? index : -1;
                }

                if (++w >= used)
                {
                    return -1;
                }

                word = ~m_Words[w];
            }
        }

        /// <summary>this &amp;= other。</summary>
        public void And(BitSet other)
        {
            ThrowIfNull(other);
            int used = WordCount(m_Length);
            int otherUsed = WordCount(other.m_Length);
            for (int w = 0; w < used; w++)
            {
                m_Words[w] &= w < otherUsed ? other.m_Words[w] : 0UL;
            }

            Recount();
        }

        /// <summary>this &amp;= ~other。</summary>
        public void AndNot(BitSet other)
        {
            ThrowIfNull(other);
            int used = Math.Min(WordCount(m_Length), WordCount(other.m_Length));
            for (int w = 0; w < used; w++)
            {
                m_Words[w] &= ~other.m_Words[w];
            }

            Recount();
        }

        /// <summary>this |= other。other 在本集长度之外有置位时：可增长模式扩容，固定长度抛出。</summary>
        public void Or(BitSet other)
        {
            PrepareForMerge(other, "Or");
            int shared = SharedWordCount(other);
            for (int w = 0; w < shared; w++)
            {
                m_Words[w] |= other.m_Words[w];
            }

            Recount();
        }

        /// <summary>this ^= other。越界规则同 <see cref="Or"/>。</summary>
        public void Xor(BitSet other)
        {
            PrepareForMerge(other, "Xor");
            int shared = SharedWordCount(other);
            for (int w = 0; w < shared; w++)
            {
                m_Words[w] ^= other.m_Words[w];
            }

            Recount();
        }

        /// <summary>内容复制为 other（长度不缩短）。越界规则同 <see cref="Or"/>。</summary>
        public void CopyFrom(BitSet other)
        {
            if (ReferenceEquals(other, this))
            {
                return;
            }

            PrepareForMerge(other, "CopyFrom");
            Array.Clear(m_Words, 0, WordCount(m_Length));
            Array.Copy(other.m_Words, m_Words, SharedWordCount(other));
            Recount();
        }

        /// <summary>两集是否有共同置位。</summary>
        public bool Intersects(BitSet other)
        {
            ThrowIfNull(other);
            int used = Math.Min(WordCount(m_Length), WordCount(other.m_Length));
            for (int w = 0; w < used; w++)
            {
                if ((m_Words[w] & other.m_Words[w]) != 0UL)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>other 的每个置位本集都有（掩码匹配：实体组件集 ⊇ 查询要求）。</summary>
        public bool IsSupersetOf(BitSet other)
        {
            ThrowIfNull(other);
            int used = WordCount(m_Length);
            int otherUsed = WordCount(other.m_Length);
            for (int w = 0; w < otherUsed; w++)
            {
                ulong mine = w < used ? m_Words[w] : 0UL;
                if ((other.m_Words[w] & ~mine) != 0UL)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>置位集合是否相同（与长度无关）。</summary>
        public bool SetEquals(BitSet other)
        {
            ThrowIfNull(other);
            if (m_Count != other.m_Count)
            {
                return false;
            }

            int used = WordCount(m_Length);
            int otherUsed = WordCount(other.m_Length);
            int max = used > otherUsed ? used : otherUsed;
            for (int w = 0; w < max; w++)
            {
                ulong mine = w < used ? m_Words[w] : 0UL;
                ulong theirs = w < otherUsed ? other.m_Words[w] : 0UL;
                if (mine != theirs)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>按升序枚举置位下标；枚举期间修改会抛出。</summary>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        private static int WordCount(int length)
        {
            return (length + 63) >> 6;
        }

        // PrepareForMerge 之后，other 在共享字之外全为 0，只需处理共享部分。
        private int SharedWordCount(BitSet other)
        {
            return Math.Min(WordCount(m_Length), WordCount(other.m_Length));
        }

        // [start, end) 非空且在 Length 内。
        private void ApplyRange(int start, int end, bool set)
        {
            int firstWord = start >> 6;
            int lastWord = (end - 1) >> 6;
            for (int w = firstWord; w <= lastWord; w++)
            {
                ulong mask = ~0UL;
                if (w == firstWord)
                {
                    mask &= ~0UL << (start & 63);
                }

                if (w == lastWord && (end & 63) != 0)
                {
                    mask &= ~0UL >> (64 - (end & 63));
                }

                ulong old = m_Words[w];
                ulong updated = set ? old | mask : old & ~mask;
                m_Count += BitMath.PopCount(updated) - BitMath.PopCount(old);
                m_Words[w] = updated;
            }

            m_Version++;
        }

        private void PrepareForMerge(BitSet other, string apiName)
        {
            ThrowIfNull(other);
            if (other.m_Length <= m_Length)
            {
                return;
            }

            if (m_Growable)
            {
                EnsureLength(other.m_Length);
            }
            else if (other.NextSet(m_Length) >= 0)
            {
                throw new FrameworkException(Utility.Text.Format(
                    "BitSet.{0}：参数在下标 {1} 处有置位，越过本集固定长度 {2}。", apiName, other.NextSet(m_Length), m_Length));
            }
        }

        private void Recount()
        {
            int used = WordCount(m_Length);
            int count = 0;
            for (int w = 0; w < used; w++)
            {
                count += BitMath.PopCount(m_Words[w]);
            }

            m_Count = count;
            m_Version++;
        }

        private static void ValidateRange(int start, int count)
        {
            if (start < 0 || count < 0 || start + count < 0)
            {
                throw new FrameworkException(Utility.Text.Format("BitSet：区间非法 start={0} count={1}。", start, count));
            }
        }

        private static void ThrowIfNegative(int index)
        {
            if (index < 0)
            {
                throw new FrameworkException(Utility.Text.Format("BitSet：下标不能为负数，实际 {0}。", index));
            }
        }

        private static void ThrowIfTooLong(long length)
        {
            if (length > MaxLength)
            {
                throw new FrameworkException(Utility.Text.Format("BitSet：长度 {0} 超过上限 {1}。", length, MaxLength));
            }
        }

        private static void ThrowIfNull(BitSet other)
        {
            if (other == null)
            {
                throw new FrameworkException("BitSet：参数不能为 null。");
            }
        }

        /// <summary>升序产出置位下标的 struct 枚举器。</summary>
        public struct Enumerator
        {
            private readonly BitSet m_Set;
            private readonly int m_Version;
            private readonly int m_WordCount;
            private int m_WordIndex;
            private ulong m_Word;
            private int m_Current;

            internal Enumerator(BitSet set)
            {
                m_Set = set;
                m_Version = set.m_Version;
                m_WordCount = WordCount(set.m_Length);
                m_WordIndex = -1;
                m_Word = 0UL;
                m_Current = -1;
            }

            public int Current
            {
                get { return m_Current; }
            }

            public bool MoveNext()
            {
                if (m_Version != m_Set.m_Version)
                {
                    throw new FrameworkException("BitSet：枚举期间集合被修改。");
                }

                while (m_Word == 0UL)
                {
                    if (++m_WordIndex >= m_WordCount)
                    {
                        return false;
                    }

                    m_Word = m_Set.m_Words[m_WordIndex];
                }

                m_Current = (m_WordIndex << 6) + BitMath.TrailingZeroCount(m_Word);
                m_Word &= m_Word - 1UL;   // 清掉最低置位
                return true;
            }
        }
    }
}
