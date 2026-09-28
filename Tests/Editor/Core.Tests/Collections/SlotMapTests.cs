//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using EjoyFramework.Core;
using NUnit.Framework;

namespace EjoyFramework.Tests.Collections
{
    public class SlotMapTests
    {
        [Test]
        public void Add_Lookup_ModifyInPlace()
        {
            var map = new SlotMap<int>();
            SlotHandle a = map.Add(10);
            SlotHandle b = map.Add(20);

            Assert.IsTrue(a.IsValid);
            Assert.AreNotEqual(a, b);
            Assert.AreEqual(2, map.Count);
            Assert.IsTrue(map.Contains(a));
            Assert.AreEqual(20, map[b]);

            map[a] += 5;
            int value;
            Assert.IsTrue(map.TryGetValue(a, out value));
            Assert.AreEqual(15, value);
        }

        [Test]
        public void DefaultHandle_IsInvalid()
        {
            var map = new SlotMap<string>();
            map.Add("x");
            Assert.IsFalse(default(SlotHandle).IsValid);
            Assert.AreEqual(SlotHandle.None, default(SlotHandle));
            Assert.IsFalse(map.Contains(SlotHandle.None), "default 句柄的代数为 0（空闲），不会命中槽位 0");
            Assert.IsFalse(map.Remove(SlotHandle.None));
            Assert.IsFalse(map.Contains(new SlotHandle(99, 1)), "越界下标");
            Assert.IsFalse(map.Contains(new SlotHandle(-1, 1)), "负下标");
        }

        [Test]
        public void Remove_InvalidatesHandle_AndReuseGetsNewGeneration()
        {
            var map = new SlotMap<string>();
            SlotHandle old = map.Add("old");
            Assert.IsTrue(map.Remove(old));
            Assert.IsFalse(map.Remove(old), "重复删除返回 false");
            Assert.IsFalse(map.Contains(old));
            Assert.Throws<FrameworkException>(() => { string unused = map[old]; });

            SlotHandle reused = map.Add("new");
            Assert.AreEqual(old.Index, reused.Index, "空闲槽位被复用");
            Assert.AreNotEqual(old.Generation, reused.Generation);
            Assert.IsFalse(map.Contains(old), "旧句柄不会误取到复用该槽位的新元素");
            string value;
            Assert.IsFalse(map.TryGetValue(old, out value));
            Assert.IsNull(value);
            Assert.AreEqual("new", map[reused]);
        }

        [Test]
        public void SwapRemove_KeepsHandlesOfMovedElement()
        {
            var map = new SlotMap<int>();
            SlotHandle a = map.Add(1);
            SlotHandle b = map.Add(2);
            SlotHandle c = map.Add(3);

            Assert.IsTrue(map.Remove(a));   // c 被挪到稠密下标 0
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(3, map[c]);
            Assert.AreEqual(2, map[b]);
            Assert.AreEqual(0, map.DenseIndexOf(c));
            Assert.AreEqual(c, map.HandleAt(0));
            Assert.AreEqual(3, map.ValueAt(0));

            int removedValue;
            Assert.IsTrue(map.Remove(c, out removedValue));
            Assert.AreEqual(3, removedValue);
            Assert.AreEqual(2, map[b]);
            Assert.AreEqual(1, map.Count);
        }

        [Test]
        public void RemoveAt_ReverseDenseLoop_RemovesMatching()
        {
            var map = new SlotMap<int>();
            var keep = new List<SlotHandle>();
            for (int i = 0; i < 20; i++)
            {
                SlotHandle h = map.Add(i);
                if (i % 3 != 0) keep.Add(h);
            }

            for (int i = map.Count - 1; i >= 0; i--)
            {
                if (map.ValueAt(i) % 3 == 0) map.RemoveAt(i);
            }

            Assert.AreEqual(keep.Count, map.Count);
            foreach (SlotHandle h in keep) Assert.AreNotEqual(0, map[h] % 3);
            Assert.Throws<FrameworkException>(() => map.RemoveAt(map.Count));
            Assert.Throws<FrameworkException>(() => map.ValueAt(-1));
        }

        [Test]
        public void MatchesDictionaryModel_UnderRandomOperations()
        {
            var map = new SlotMap<int>(4);
            var model = new Dictionary<SlotHandle, int>();
            var live = new List<SlotHandle>();
            var dead = new List<SlotHandle>();
            var random = new Random(4242);
            for (int step = 0; step < 5000; step++)
            {
                if (live.Count == 0 || random.Next(3) != 0)
                {
                    SlotHandle h = map.Add(step);
                    Assert.IsFalse(model.ContainsKey(h), "新句柄不与存活句柄重复");
                    model.Add(h, step);
                    live.Add(h);
                }
                else
                {
                    int pick = random.Next(live.Count);
                    SlotHandle h = live[pick];
                    live[pick] = live[live.Count - 1];
                    live.RemoveAt(live.Count - 1);
                    Assert.IsTrue(map.Remove(h));
                    model.Remove(h);
                    dead.Add(h);
                }
            }

            Assert.AreEqual(model.Count, map.Count);
            foreach (KeyValuePair<SlotHandle, int> kv in model) Assert.AreEqual(kv.Value, map[kv.Key]);
            foreach (SlotHandle h in dead) Assert.IsFalse(map.Contains(h));

            int enumerated = 0;
            foreach (int v in map)
            {
                Assert.GreaterOrEqual(v, 0);
                enumerated++;
            }
            Assert.AreEqual(map.Count, enumerated);
            Assert.AreEqual(map.Count, map.AsSpan().Length);
        }

        [Test]
        public void Clear_InvalidatesAllHandles_KeepsCapacity()
        {
            var map = new SlotMap<object>(4);
            var handles = new SlotHandle[10];
            for (int i = 0; i < handles.Length; i++) handles[i] = map.Add(new object());
            int capacity = map.Capacity;

            map.Clear();
            Assert.AreEqual(0, map.Count);
            Assert.AreEqual(capacity, map.Capacity);
            foreach (SlotHandle h in handles) Assert.IsFalse(map.Contains(h));

            SlotHandle again = map.Add("again");
            Assert.AreEqual("again", map[again]);
        }

        [Test]
        public void GenerationExhausted_SlotRetired_NeverReused()
        {
            var map = new SlotMap<int>(4);
            SlotHandle first = map.Add(1);
            map.SetGenerationForTest(first.Index, uint.MaxValue);   // 奇数：仍占用，代数已到上限
            SlotHandle last = map.HandleAt(0);
            Assert.AreEqual(uint.MaxValue, last.Generation);
            Assert.IsFalse(map.Contains(first));

            Assert.IsTrue(map.Remove(last));   // 代数回绕到 0 → 退役
            Assert.IsFalse(map.Contains(last));
            Assert.IsFalse(map.Contains(new SlotHandle(first.Index, 0u)));

            SlotHandle next = map.Add(2);
            Assert.AreNotEqual(first.Index, next.Index, "退役槽位不进空闲链");
            Assert.AreEqual(2, map[next]);
        }

        [Test]
        public void Grow_KeepsAllHandlesValid()
        {
            var map = new SlotMap<int>(4);
            var handles = new SlotHandle[100];
            for (int i = 0; i < handles.Length; i++) handles[i] = map.Add(i * 2);
            Assert.GreaterOrEqual(map.Capacity, 100);
            for (int i = 0; i < handles.Length; i++) Assert.AreEqual(i * 2, map[handles[i]]);

            map.EnsureCapacity(1000);
            Assert.GreaterOrEqual(map.Capacity, 1000);
            Assert.AreEqual(198, map[handles[99]]);
        }

        [Test]
        public void Handle_PackedRoundTrip_AndEquality()
        {
            var map = new SlotMap<int>();
            map.Remove(map.Add(0));
            SlotHandle h = map.Add(1);   // 代数 3
            Assert.AreEqual(3u, h.Generation);

            SlotHandle back = SlotHandle.FromPacked(h.Packed);
            Assert.AreEqual(h, back);
            Assert.IsTrue(h == back);
            Assert.IsFalse(h != back);
            Assert.AreEqual(h.GetHashCode(), back.GetHashCode());
            Assert.IsTrue(h.Equals((object)back));
            Assert.AreEqual(1, map[back]);

            var high = new SlotHandle(int.MaxValue, uint.MaxValue);
            Assert.AreEqual(high, SlotHandle.FromPacked(high.Packed));
        }

        [Test]
        public void Enumerator_RefValues_CurrentHandle_AndDetectsModification()
        {
            var map = new SlotMap<int>();
            SlotHandle a = map.Add(1);
            SlotHandle b = map.Add(2);

            var seen = new List<SlotHandle>();
            var e = map.GetEnumerator();
            while (e.MoveNext())
            {
                e.Current *= 10;
                seen.Add(e.CurrentHandle);
            }

            CollectionAssert.AreEquivalent(new[] { a, b }, seen);
            Assert.AreEqual(10, map[a]);
            Assert.AreEqual(20, map[b]);

            Assert.Throws<FrameworkException>(() =>
            {
                foreach (int v in map) map.Add(v);
            });
        }

        [Test]
        public void SteadyState_AddLookupRemove_ZeroAlloc()
        {
            var map = new SlotMap<long>(256);
            var handles = new SlotHandle[128];
            long sink = 0;
            ZeroAlloc.Assert(() =>
            {
                for (int i = 0; i < handles.Length; i++) handles[i] = map.Add(i);
                for (int i = 0; i < handles.Length; i++)
                {
                    long v;
                    if (map.TryGetValue(handles[i], out v)) sink += v;
                    if (map.Contains(handles[i])) sink++;
                }

                foreach (ref long v in map) sink += v;
                for (int i = 0; i < handles.Length; i += 2) map.Remove(handles[i]);
                for (int i = map.Count - 1; i >= 0; i--) map.RemoveAt(i);
            });
            Assert.AreEqual(0, map.Count);
            Assert.Greater(sink, 0L);
        }
    }
}
