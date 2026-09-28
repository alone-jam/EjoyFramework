//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using EjoyFramework.Core;
using NUnit.Framework;

namespace EjoyFramework.Tests.Collections
{
    public class RingBufferTests
    {
        private struct Snapshot
        {
            public int Tick;
            public float X;
            public float Y;
            public float Z;
            public long Payload0;
            public long Payload1;
        }

        [Test]
        public void Push_Pop_IsFifo()
        {
            var ring = new RingBuffer<int>(4);
            for (int i = 0; i < 4; i++) ring.Push(i);

            Assert.AreEqual(4, ring.Count);
            Assert.IsTrue(ring.IsFull);
            for (int i = 0; i < 4; i++) Assert.AreEqual(i, ring.PopFront());
            Assert.IsTrue(ring.IsEmpty);
        }

        [Test]
        public void Wraparound_KeepsLogicalOrder()
        {
            var ring = new RingBuffer<int>(3, RingBufferOverflow.Reject);
            ring.Push(1);
            ring.Push(2);
            Assert.AreEqual(1, ring.PopFront());
            ring.Push(3);
            ring.Push(4);   // 物理上回绕到下标 0

            Assert.AreEqual(3, ring.Count);
            Assert.AreEqual(2, ring[0]);
            Assert.AreEqual(3, ring[1]);
            Assert.AreEqual(4, ring[2]);
            Assert.AreEqual(2, ring.PeekFront());
            Assert.AreEqual(4, ring.PeekBack());
        }

        [Test]
        public void Grow_DoublesCapacity_AndPreservesOrderAcrossWrap()
        {
            var ring = new RingBuffer<int>(4);
            for (int i = 0; i < 4; i++) ring.Push(i);
            ring.PopFront();
            ring.PopFront();
            ring.Push(4);
            ring.Push(5);   // 已回绕且满
            ring.Push(6);   // 触发扩容

            Assert.AreEqual(8, ring.Capacity);
            int[] expected = { 2, 3, 4, 5, 6 };
            Assert.AreEqual(expected.Length, ring.Count);
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], ring[i]);
        }

        [Test]
        public void Overwrite_EvictsOldest_AndAdvancesHeadSequence()
        {
            var ring = new RingBuffer<int>(3, RingBufferOverflow.Overwrite);
            for (int i = 0; i < 5; i++) Assert.IsTrue(ring.TryPush(i * 10));

            Assert.AreEqual(3, ring.Capacity);
            Assert.AreEqual(3, ring.Count);
            Assert.AreEqual(20, ring[0]);
            Assert.AreEqual(40, ring[2]);
            Assert.AreEqual(2, ring.HeadSequence);
            Assert.AreEqual(5, ring.NextSequence);
        }

        [Test]
        public void Reject_TryPushReturnsFalse_PushThrows()
        {
            var ring = new RingBuffer<int>(2, RingBufferOverflow.Reject);
            Assert.IsTrue(ring.TryPush(1));
            Assert.IsTrue(ring.TryPush(2));
            Assert.IsFalse(ring.TryPush(3));
            Assert.Throws<FrameworkException>(() => ring.Push(3));
            Assert.Throws<FrameworkException>(() => ring.PushRef());
            Assert.AreEqual(2, ring.Count);
            Assert.AreEqual(1, ring.PeekFront());
        }

        [Test]
        public void EmptyAccess_Throws_TryVariantsReturnFalse()
        {
            var ring = new RingBuffer<int>(2);
            Assert.Throws<FrameworkException>(() => ring.PopFront());
            Assert.Throws<FrameworkException>(() => ring.PopBack());
            Assert.Throws<FrameworkException>(() => ring.PeekFront());
            Assert.Throws<FrameworkException>(() => ring.PeekBack());
            Assert.Throws<FrameworkException>(() => { int unused = ring[0]; });
            int item;
            Assert.IsFalse(ring.TryPopFront(out item));
            Assert.IsFalse(ring.TryPopBack(out item));
            Assert.Throws<FrameworkException>(() => new RingBuffer<int>(0));
        }

        [Test]
        public void PopBack_RemovesNewest()
        {
            var ring = new RingBuffer<int>(4);
            ring.Push(1);
            ring.Push(2);
            ring.Push(3);
            Assert.AreEqual(3, ring.PopBack());
            Assert.AreEqual(3, ring.NextSequence - ring.HeadSequence + 1);
            Assert.AreEqual(2, ring.PeekBack());
            Assert.AreEqual(2, ring.NextSequence, "PopBack 回退序号：下一次写入复用被回滚的序号");
        }

        [Test]
        public void SequenceAddressing_AlignedToTicks()
        {
            var history = new RingBuffer<Snapshot>(8, RingBufferOverflow.Overwrite);
            history.Clear(1000);
            for (int tick = 1000; tick < 1012; tick++)
            {
                ref Snapshot s = ref history.PushRef();
                s.Tick = tick;
                s.X = tick * 0.5f;
            }

            // 容量 8：只保留 1004..1011。
            Assert.AreEqual(1004, history.HeadSequence);
            Assert.AreEqual(1012, history.NextSequence);
            Assert.IsFalse(history.ContainsSequence(1003));
            Assert.IsTrue(history.ContainsSequence(1004));
            Assert.IsTrue(history.ContainsSequence(1011));
            Assert.IsFalse(history.ContainsSequence(1012));

            Snapshot found;
            Assert.IsTrue(history.TryGetAtSequence(1007, out found));
            Assert.AreEqual(1007, found.Tick);
            Assert.IsFalse(history.TryGetAtSequence(1012, out found));
            Assert.AreEqual(1010, history.AtSequence(1010).Tick);
            Assert.Throws<FrameworkException>(() => history.AtSequence(999));

            // 原地修改（ref）。
            history.AtSequence(1008).Y = 42f;
            Assert.AreEqual(42f, history.AtSequence(1008).Y);
        }

        [Test]
        public void DiscardBefore_And_DiscardFrom_ForReconcileAndRollback()
        {
            var inputs = new RingBuffer<int>(16);
            inputs.Clear(50);
            for (int tick = 50; tick < 60; tick++) inputs.Push(tick);

            Assert.AreEqual(3, inputs.DiscardBefore(53));   // 50..52 已确认
            Assert.AreEqual(53, inputs.HeadSequence);
            Assert.AreEqual(53, inputs.PeekFront());

            Assert.AreEqual(3, inputs.DiscardFrom(57));     // 回滚 57..59
            Assert.AreEqual(57, inputs.NextSequence);
            Assert.AreEqual(56, inputs.PeekBack());

            Assert.AreEqual(0, inputs.DiscardBefore(10), "早于头部的序号不丢任何元素");
            Assert.AreEqual(0, inputs.DiscardFrom(100), "晚于尾部的序号不丢任何元素");

            Assert.AreEqual(4, inputs.DiscardBefore(1000), "超过最新元素：全部丢弃");
            Assert.AreEqual(0, inputs.Count);
            Assert.AreEqual(57, inputs.HeadSequence, "序号只由写入推进，不跳到参数值");
            inputs.Push(7);
            Assert.AreEqual(57, inputs.HeadSequence);
        }

        [Test]
        public void Sequence_WrapsAroundIntMaxValue()
        {
            var ring = new RingBuffer<int>(4, RingBufferOverflow.Overwrite);
            ring.Clear(int.MaxValue - 1);
            ring.Push(1);   // int.MaxValue - 1
            ring.Push(2);   // int.MaxValue
            ring.Push(3);   // int.MinValue
            ring.Push(4);   // int.MinValue + 1

            Assert.AreEqual(3, ring.AtSequence(int.MinValue));
            Assert.AreEqual(2, ring.AtSequence(int.MaxValue));
            Assert.IsTrue(ring.ContainsSequence(int.MinValue + 1));
            Assert.IsFalse(ring.ContainsSequence(int.MinValue + 2));
            Assert.AreEqual(2, ring.DiscardBefore(int.MinValue));
            Assert.AreEqual(int.MinValue, ring.HeadSequence);
        }

        [Test]
        public void Clear_ContinuesSequence_ClearWithValueRealigns()
        {
            var ring = new RingBuffer<string>(4);
            ring.Push("a");
            ring.Push("b");
            ring.Clear();
            Assert.AreEqual(0, ring.Count);
            Assert.AreEqual(2, ring.HeadSequence);

            ring.Clear(77);
            ring.Push("c");
            Assert.AreEqual("c", ring.AtSequence(77));
        }

        [Test]
        public void Enumerator_OldestToNewest_AndDetectsModification()
        {
            var ring = new RingBuffer<int>(3, RingBufferOverflow.Overwrite);
            for (int i = 1; i <= 5; i++) ring.Push(i);

            int expected = 3;
            foreach (int v in ring)
            {
                Assert.AreEqual(expected++, v);
            }
            Assert.AreEqual(6, expected);

            foreach (ref int v in ring)
            {
                v *= 10;   // 原地修改不算结构修改
            }
            Assert.AreEqual(30, ring[0]);

            Assert.Throws<FrameworkException>(() =>
            {
                foreach (int v in ring)
                {
                    ring.Push(v);
                }
            });
        }

        [Test]
        public void CopyTo_WritesLogicalOrder()
        {
            var ring = new RingBuffer<int>(4, RingBufferOverflow.Overwrite);
            for (int i = 0; i < 6; i++) ring.Push(i);
            int[] dst = new int[4];
            Assert.AreEqual(4, ring.CopyTo(dst));
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, dst);
            Assert.Throws<FrameworkException>(() => ring.CopyTo(new int[3]));
        }

        [Test]
        public void PopFront_ReleasesReferences()
        {
            var ring = new RingBuffer<object>(2);
            var weak = PushAndPopUnreachable(ring);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(weak.IsAlive, "取出后槽位应清为 default，不延长引用寿命");
        }

        // 不内联：否则 payload 可能留在调用方栈帧里，被保守式 GC（Boehm）当作存活。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference PushAndPopUnreachable(RingBuffer<object> ring)
        {
            object payload = new object();
            ring.Push(payload);
            ring.PopFront();
            return new WeakReference(payload);
        }

        [Test]
        public void SteadyState_PushPopAndSequenceLookup_ZeroAlloc()
        {
            var history = new RingBuffer<Snapshot>(64, RingBufferOverflow.Overwrite);
            var fifo = new RingBuffer<int>(16);
            int tick = 0;
            long sink = 0;
            ZeroAlloc.Assert(() =>
            {
                for (int i = 0; i < 128; i++)
                {
                    ref Snapshot s = ref history.PushRef();
                    s.Tick = tick++;
                    Snapshot found;
                    if (history.TryGetAtSequence(tick - 32, out found)) sink += found.Tick;
                    fifo.Push(i);
                    if (fifo.Count > 8) sink += fifo.PopFront();
                }

                history.DiscardBefore(tick - 16);
                foreach (ref Snapshot s in history) sink += s.Tick;
            });
            Assert.Greater(sink, 0L);
        }
    }
}
