//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System.Collections.Generic;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 零分配原地排序。
    ///
    /// 为什么不用 <c>List&lt;T&gt;.Sort</c>：Unity 的 Mono（referencesource 谱系）里
    /// <c>ArraySortHelper&lt;T&gt;.Sort(keys, index, length, comparer)</c> 会把 <c>comparer.Compare</c>
    /// 包成一个 <c>Comparison&lt;T&gt;</c> 委托再做 introsort——每次排序都分配；
    /// <c>Sort(Comparison&lt;T&gt;)</c> 重载则额外包一层 FunctorComparer。两条路都逃不掉分配。
    /// 热路径（对象池收缩、按优先级遍历、调度队列整理）用本类：堆排序 O(n log n)、原地、无递归、无委托。
    ///
    /// 非稳定排序（相等元素相对顺序不保证），需要稳定性的调用方自行把序号编进比较键。
    /// </summary>
    public static class NoAllocSort
    {
        /// <summary>小于该长度时插入排序（常数更小，且对近乎有序的输入接近 O(n)）。</summary>
        private const int InsertionSortThreshold = 16;

        /// <summary>原地升序排序整个列表。</summary>
        public static void Sort<T>(List<T> list, IComparer<T> comparer)
        {
            if (list == null)
            {
                throw new FrameworkException("NoAllocSort.Sort：list 不能为 null。");
            }

            Sort(list, 0, list.Count, comparer);
        }

        /// <summary>原地升序排序 [index, index + count)。</summary>
        public static void Sort<T>(List<T> list, int index, int count, IComparer<T> comparer)
        {
            if (list == null)
            {
                throw new FrameworkException("NoAllocSort.Sort：list 不能为 null。");
            }

            if (comparer == null)
            {
                throw new FrameworkException("NoAllocSort.Sort：comparer 不能为 null。");
            }

            if (index < 0 || count < 0 || index + count > list.Count)
            {
                throw new FrameworkException(Utility.Text.Format("NoAllocSort.Sort：区间越界，index={0}，count={1}，list.Count={2}。", index, count, list.Count));
            }

            if (count < 2)
            {
                return;
            }

            if (count <= InsertionSortThreshold)
            {
                InsertionSort(list, index, count, comparer);
                return;
            }

            HeapSort(list, index, count, comparer);
        }

        private static void InsertionSort<T>(List<T> list, int index, int count, IComparer<T> comparer)
        {
            int end = index + count;
            for (int i = index + 1; i < end; i++)
            {
                T key = list[i];
                int j = i - 1;
                while (j >= index && comparer.Compare(list[j], key) > 0)
                {
                    list[j + 1] = list[j];
                    j--;
                }

                list[j + 1] = key;
            }
        }

        private static void HeapSort<T>(List<T> list, int index, int count, IComparer<T> comparer)
        {
            // 以 index 为根偏移的最大堆：先建堆，再逐个把堆顶换到末尾并收缩。
            for (int i = count / 2 - 1; i >= 0; i--)
            {
                SiftDown(list, index, i, count, comparer);
            }

            for (int last = count - 1; last > 0; last--)
            {
                T tmp = list[index];
                list[index] = list[index + last];
                list[index + last] = tmp;
                SiftDown(list, index, 0, last, comparer);
            }
        }

        private static void SiftDown<T>(List<T> list, int index, int root, int size, IComparer<T> comparer)
        {
            while (true)
            {
                int child = root * 2 + 1;
                if (child >= size)
                {
                    return;
                }

                int right = child + 1;
                if (right < size && comparer.Compare(list[index + right], list[index + child]) > 0)
                {
                    child = right;
                }

                if (comparer.Compare(list[index + root], list[index + child]) >= 0)
                {
                    return;
                }

                T tmp = list[index + root];
                list[index + root] = list[index + child];
                list[index + child] = tmp;
                root = child;
            }
        }
    }
}
