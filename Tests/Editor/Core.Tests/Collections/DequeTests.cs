//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EjoyFramework.Core;
using NUnit.Framework;

namespace EjoyFramework.Tests.Collections
{
    public class DequeTests
    {
        [TestCase(4, 0, 3, 3)]
        [TestCase(4, 3, 0, 3)]
        [TestCase(4, 3, 1, 0)]
        [TestCase(1500000000, 1000000000, 1200000000, 700000000)]
        [TestCase(2147483646, 2147483645, 2147483645, 2147483644)]
        public void PhysicalIndex_LargeCapacity_DoesNotOverflow(int capacity, int head, int logicalIndex, int expected)
        {
            // 直接验证实际寻址使用的算术，避免为边界测试分配 GB 级数组。
            Assert.AreEqual(expected, Deque<byte>.PhysicalIndex(head, logicalIndex, capacity));
        }

        [Test]
        public void PushBack_PopFront_IsFifo()
        {
            var deque = new Deque<int>(4);
            for (int i = 0; i < 4; i++) deque.PushBack(i);

            Assert.AreEqual(4, deque.Count);
            for (int i = 0; i < 4; i++) Assert.AreEqual(i, deque.PopFront());
            Assert.AreEqual(0, deque.Count);
        }

        [Test]
        public void PushFront_PopFront_IsLifo()
        {
            var deque = new Deque<int>(4);
            for (int i = 0; i < 4; i++) deque.PushFront(i);

            for (int i = 3; i >= 0; i--) Assert.AreEqual(i, deque.PopFront());
        }

        [Test]
        public void BothEnds_KeepLogicalOrder()
        {
            var deque = new Deque<int>(4);
            deque.PushBack(2);
            deque.PushFront(1);   // 头部回绕到物理末尾
            deque.PushBack(3);
            deque.PushFront(0);

            Assert.AreEqual(4, deque.Count);
            for (int i = 0; i < 4; i++) Assert.AreEqual(i, deque[i]);
            Assert.AreEqual(0, deque.PeekFront());
            Assert.AreEqual(3, deque.PeekBack());
            Assert.AreEqual(3, deque.PopBack());
            Assert.AreEqual(0, deque.PopFront());
            Assert.AreEqual(1, deque[0]);
            Assert.AreEqual(2, deque[1]);
        }

        [Test]
        public void Grow_WhileWrapped_PreservesOrder()
        {
            var deque = new Deque<int>(4);
            deque.PushBack(2);
            deque.PushBack(3);
            deque.PushFront(1);
            deque.PushFront(0);   // 已满且头部回绕
            deque.PushFront(-1);  // 触发扩容
            deque.PushBack(4);

            Assert.AreEqual(8, deque.Capacity);
            Assert.AreEqual(6, deque.Count);
            for (int i = 0; i < 6; i++) Assert.AreEqual(i - 1, deque[i]);
        }

        [Test]
        public void MatchesListModel_UnderRandomOperations()
        {
            var deque = new Deque<int>(4);
            var model = new List<int>();
            var random = new Random(20260928);
            for (int step = 0; step < 5000; step++)
            {
                int op = random.Next(4);
                if (op == 0) { deque.PushBack(step); model.Add(step); }
                else if (op == 1) { deque.PushFront(step); model.Insert(0, step); }
                else if (op == 2 && model.Count > 0) { Assert.AreEqual(model[0], deque.PopFront()); model.RemoveAt(0); }
                else if (op == 3 && model.Count > 0) { Assert.AreEqual(model[model.Count - 1], deque.PopBack()); model.RemoveAt(model.Count - 1); }

                Assert.AreEqual(model.Count, deque.Count);
            }

            for (int i = 0; i < model.Count; i++) Assert.AreEqual(model[i], deque[i]);
        }

        [Test]
        public void EmptyAccess_Throws_TryVariantsReturnFalse()
        {
            var deque = new Deque<int>();
            Assert.Throws<FrameworkException>(() => deque.PopFront());
            Assert.Throws<FrameworkException>(() => deque.PopBack());
            Assert.Throws<FrameworkException>(() => deque.PeekFront());
            Assert.Throws<FrameworkException>(() => deque.PeekBack());
            Assert.Throws<FrameworkException>(() => { int unused = deque[0]; });
            int item;
            Assert.IsFalse(deque.TryPopFront(out item));
            Assert.IsFalse(deque.TryPopBack(out item));

            deque.PushBack(7);
            Assert.IsTrue(deque.TryPopBack(out item));
            Assert.AreEqual(7, item);
        }

        [Test]
        public void Indexer_ReturnsReference()
        {
            var deque = new Deque<int>(4);
            deque.PushBack(1);
            deque.PushBack(2);
            deque[1] = 20;
            deque.PeekFront() = 10;
            Assert.AreEqual(10, deque[0]);
            Assert.AreEqual(20, deque[1]);
        }

        [Test]
        public void Clear_KeepsCapacity_TrimExcess_Shrinks()
        {
            var deque = new Deque<string>(4);
            for (int i = 0; i < 10; i++) deque.PushBack("x" + i);
            int capacity = deque.Capacity;

            deque.Clear();
            Assert.AreEqual(0, deque.Count);
            Assert.AreEqual(capacity, deque.Capacity);

            deque.PushBack("a");
            deque.TrimExcess();
            Assert.AreEqual(4, deque.Capacity, "收缩到 max(Count, 4)");
            Assert.AreEqual("a", deque.PeekFront());

            deque.EnsureCapacity(33);
            Assert.GreaterOrEqual(deque.Capacity, 33);
            Assert.AreEqual("a", deque.PeekFront());
        }

        [Test]
        public void CopyTo_WritesFrontToBack()
        {
            var deque = new Deque<int>(4);
            deque.PushBack(2);
            deque.PushFront(1);
            deque.PushBack(3);
            int[] dst = new int[3];
            Assert.AreEqual(3, deque.CopyTo(dst));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, dst);
            Assert.Throws<FrameworkException>(() => deque.CopyTo(new int[2]));
        }

        [Test]
        public void Enumerator_FrontToBack_AndDetectsModification()
        {
            var deque = new Deque<int>(4);
            deque.PushBack(1);
            deque.PushFront(0);
            deque.PushBack(2);

            int expected = 0;
            foreach (int v in deque) Assert.AreEqual(expected++, v);
            Assert.AreEqual(3, expected);

            foreach (ref int v in deque) v += 10;   // 原地修改不算结构修改
            Assert.AreEqual(10, deque[0]);

            Assert.Throws<FrameworkException>(() =>
            {
                foreach (int v in deque) deque.PushFront(v);
            });
        }

        [Test]
        public void Pop_ReleasesReferences()
        {
            var deque = new Deque<object>(4);
            var front = PushAndPopUnreachable(deque, true);
            var back = PushAndPopUnreachable(deque, false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(front.IsAlive, "PopFront 后槽位应清为 default");
            Assert.IsFalse(back.IsAlive, "PopBack 后槽位应清为 default");
        }

        // 不内联：否则 payload 可能留在调用方栈帧里，被保守式 GC（Boehm）当作存活。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference PushAndPopUnreachable(Deque<object> deque, bool front)
        {
            object payload = new object();
            deque.PushBack(payload);
            if (front) deque.PopFront();
            else deque.PopBack();
            return new WeakReference(payload);
        }

        [Test]
        public void SteadyState_BothEnds_ZeroAlloc()
        {
            var deque = new Deque<long>(64);
            long sink = 0;
            ZeroAlloc.Assert(() =>
            {
                for (int i = 0; i < 32; i++)
                {
                    deque.PushBack(i);
                    deque.PushFront(-i);
                }

                sink += deque[deque.Count / 2];
                foreach (long v in deque) sink += v;
                while (deque.Count > 0)
                {
                    sink += deque.PopFront();
                    long back;
                    if (deque.TryPopBack(out back)) sink += back;
                }
            });
            Assert.AreEqual(0, deque.Count);
            Assert.AreNotEqual(long.MinValue, sink);
        }
    }
}
