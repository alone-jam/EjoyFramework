//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using EjoyFramework.Core;
using NUnit.Framework;

namespace EjoyFramework.Tests.Collections
{
    public class BitSetTests
    {
        [Test]
        public void BitMath_TrailingZeroCount_EveryBit()
        {
            Assert.AreEqual(64, BitMath.TrailingZeroCount(0UL));
            for (int bit = 0; bit < 64; bit++)
            {
                ulong single = 1UL << bit;
                Assert.AreEqual(bit, BitMath.TrailingZeroCount(single), "bit " + bit);
                Assert.AreEqual(bit, BitMath.TrailingZeroCount(single | (single << 1) | (bit == 63 ? 0UL : 1UL << 63)), "高位不影响, bit " + bit);
            }
        }

        [Test]
        public void BitMath_PopCount_MatchesNaive()
        {
            var random = new Random(64);
            var bytes = new byte[8];
            Assert.AreEqual(0, BitMath.PopCount(0UL));
            Assert.AreEqual(64, BitMath.PopCount(ulong.MaxValue));
            for (int i = 0; i < 1000; i++)
            {
                random.NextBytes(bytes);
                ulong value = BitConverter.ToUInt64(bytes, 0);
                int naive = 0;
                for (ulong v = value; v != 0UL; v >>= 1) naive += (int)(v & 1UL);
                Assert.AreEqual(naive, BitMath.PopCount(value));
            }
        }

        [Test]
        public void SetClearTest_ReportTransitions_AndMaintainCount()
        {
            var bits = new BitSet(130);
            Assert.IsTrue(bits.Set(0));
            Assert.IsTrue(bits.Set(64));
            Assert.IsTrue(bits.Set(129));
            Assert.IsFalse(bits.Set(64), "已置位：返回 false");
            Assert.AreEqual(3, bits.Count);

            Assert.IsTrue(bits.Test(129));
            Assert.IsFalse(bits.Test(128));
            Assert.IsTrue(bits[64]);

            Assert.IsTrue(bits.Clear(64));
            Assert.IsFalse(bits.Clear(64), "已清位：返回 false");
            bits[5] = true;
            bits[0] = false;
            Assert.AreEqual(2, bits.Count);
            Assert.IsTrue(bits.Test(5));
        }

        [Test]
        public void FixedLength_OutOfRangeRules()
        {
            var bits = new BitSet(10);
            Assert.Throws<FrameworkException>(() => bits.Set(10));
            Assert.IsFalse(bits.Test(10), "越界 Test 返回 false");
            Assert.IsFalse(bits.Clear(1000), "越界 Clear 是空操作");
            Assert.Throws<FrameworkException>(() => bits.Test(-1));
            Assert.Throws<FrameworkException>(() => bits.Set(-1));
            Assert.Throws<FrameworkException>(() => bits.Clear(-1));
            Assert.Throws<FrameworkException>(() => bits.SetRange(5, 6));
            Assert.Throws<FrameworkException>(() => new BitSet(-1));

            bits.EnsureLength(100);
            Assert.AreEqual(100, bits.Length);
            Assert.IsTrue(bits.Set(99), "显式 EnsureLength 后可写");
        }

        [Test]
        public void Growable_SetBeyondLength_Grows()
        {
            var bits = new BitSet(0, growable: true);
            Assert.IsTrue(bits.IsGrowable);
            Assert.IsTrue(bits.Set(1000));
            Assert.AreEqual(1001, bits.Length);
            Assert.AreEqual(1, bits.Count);
            Assert.AreEqual(1000, bits.FirstSet());

            bits.SetRange(2000, 10);
            Assert.AreEqual(2010, bits.Length);
            Assert.AreEqual(11, bits.Count);
        }

        [Test]
        public void LengthBeyondMax_ThrowsFrameworkException()
        {
            var bits = new BitSet(0, growable: true);
            Assert.Throws<FrameworkException>(() => bits.Set(int.MaxValue), "index + 1 不能溢出成负数而跳过扩容");
            Assert.Throws<FrameworkException>(() => bits.EnsureLength(BitSet.MaxLength + 1));
            Assert.Throws<FrameworkException>(() => new BitSet(int.MaxValue));
            Assert.AreEqual(0, bits.Length);
        }

        [Test]
        public void NextSet_And_NextClear_AcrossWords()
        {
            var bits = new BitSet(200);
            Assert.AreEqual(-1, bits.FirstSet());
            Assert.AreEqual(0, bits.FirstClear());

            bits.Set(3);
            bits.Set(63);
            bits.Set(64);
            bits.Set(199);
            Assert.AreEqual(3, bits.FirstSet());
            Assert.AreEqual(63, bits.NextSet(4));
            Assert.AreEqual(64, bits.NextSet(64));
            Assert.AreEqual(199, bits.NextSet(65));
            Assert.AreEqual(-1, bits.NextSet(200));
            Assert.AreEqual(-1, bits.NextSet(5000));

            Assert.AreEqual(62, bits.NextClear(62));
            Assert.AreEqual(65, bits.NextClear(63));
            Assert.Throws<FrameworkException>(() => bits.NextSet(-1));
        }

        [Test]
        public void NextClear_IgnoresPaddingBitsOfLastWord()
        {
            var bits = new BitSet(70);
            bits.SetAll();
            Assert.AreEqual(70, bits.Count);
            Assert.AreEqual(-1, bits.FirstClear(), "最后一个字里 70..127 不属于位集");

            bits.EnsureLength(128);
            Assert.AreEqual(70, bits.Count, "扩长后新增位为 0");
            Assert.AreEqual(70, bits.FirstClear());
            Assert.AreEqual(-1, bits.NextSet(70));
        }

        [Test]
        public void Ranges_MatchBoolModel()
        {
            var bits = new BitSet(300);
            var model = new bool[300];
            var random = new Random(300);
            for (int step = 0; step < 400; step++)
            {
                int start = random.Next(300);
                int count = random.Next(300 - start + 1);
                bool set = random.Next(2) == 0;
                if (set) bits.SetRange(start, count);
                else bits.ClearRange(start, count);
                for (int i = start; i < start + count; i++) model[i] = set;

                AssertMatches(bits, model);
            }

            bits.ClearRange(250, 1000);   // 越过 Length 的部分忽略
            for (int i = 250; i < 300; i++) model[i] = false;
            AssertMatches(bits, model);
        }

        [Test]
        public void SingleBitOps_MatchBoolModel_Growable()
        {
            var bits = new BitSet(1, growable: true);
            var model = new bool[1024];
            var random = new Random(1024);
            for (int step = 0; step < 5000; step++)
            {
                int index = random.Next(1024);
                if (random.Next(3) == 0)
                {
                    Assert.AreEqual(model[index] && index < bits.Length, bits.Clear(index));
                    model[index] = false;
                }
                else
                {
                    Assert.AreEqual(!model[index], bits.Set(index));
                    model[index] = true;
                }
            }

            bits.EnsureLength(1024);
            AssertMatches(bits, model);
        }

        [Test]
        public void BulkOps_AndOrXorAndNot_WithDifferentLengths()
        {
            var a = FromIndices(130, 1, 64, 100, 129);
            var b = FromIndices(70, 1, 65, 69);

            var and = FromIndices(130, 1, 64, 100, 129);
            and.And(b);
            AssertIndices(and, 1);

            var andNot = FromIndices(130, 1, 64, 100, 129);
            andNot.AndNot(b);
            AssertIndices(andNot, 64, 100, 129);

            var or = FromIndices(130, 1, 64, 100, 129);
            or.Or(b);
            AssertIndices(or, 1, 64, 65, 69, 100, 129);

            var xor = FromIndices(130, 1, 64, 100, 129);
            xor.Xor(b);
            AssertIndices(xor, 64, 65, 69, 100, 129);

            var copy = new BitSet(130);
            copy.Set(7);
            copy.CopyFrom(b);
            AssertIndices(copy, 1, 65, 69);
            Assert.AreEqual(130, copy.Length, "CopyFrom 不缩短长度");

            Assert.IsTrue(a.Intersects(b));
            Assert.IsFalse(FromIndices(64, 2).Intersects(b));
        }

        [Test]
        public void Or_FixedLength_RejectsBitsBeyondLength_GrowableExtends()
        {
            var shortFixed = FromIndices(10, 1);
            Assert.DoesNotThrow(() => shortFixed.Or(FromIndices(200, 3)), "参数更长但越界部分全为 0：允许");
            AssertIndices(shortFixed, 1, 3);
            Assert.Throws<FrameworkException>(() => shortFixed.Or(FromIndices(200, 150)));
            Assert.Throws<FrameworkException>(() => shortFixed.Xor(FromIndices(200, 150)));
            Assert.Throws<FrameworkException>(() => shortFixed.CopyFrom(FromIndices(200, 150)));

            var growable = new BitSet(10, growable: true);
            growable.Or(FromIndices(200, 150));
            Assert.AreEqual(200, growable.Length);
            AssertIndices(growable, 150);
            Assert.Throws<FrameworkException>(() => growable.Or(null));
        }

        [Test]
        public void SupersetAndEquality_IgnoreLength()
        {
            var entity = FromIndices(256, 1, 5, 70, 200);
            var query = FromIndices(80, 5, 70);
            Assert.IsTrue(entity.IsSupersetOf(query));
            Assert.IsFalse(query.IsSupersetOf(entity));
            query.Set(71);
            Assert.IsFalse(entity.IsSupersetOf(query));
            Assert.IsTrue(entity.IsSupersetOf(new BitSet(0)));

            Assert.IsTrue(FromIndices(64, 3, 9).SetEquals(FromIndices(1000, 3, 9)));
            Assert.IsFalse(FromIndices(64, 3, 9).SetEquals(FromIndices(1000, 3, 900)));
        }

        [Test]
        public void Enumerator_YieldsAscending_AndDetectsModification()
        {
            var bits = FromIndices(300, 0, 63, 64, 127, 128, 299);
            int[] expected = { 0, 63, 64, 127, 128, 299 };
            int n = 0;
            foreach (int index in bits) Assert.AreEqual(expected[n++], index);
            Assert.AreEqual(expected.Length, n);

            Assert.Throws<FrameworkException>(() =>
            {
                foreach (int index in bits) bits.Clear(index);
            });

            // 需要边遍历边改：NextSet 循环。
            for (int i = bits.FirstSet(); i >= 0; i = bits.NextSet(i + 1)) bits.Clear(i);
            Assert.IsTrue(bits.IsEmpty);
        }

        [Test]
        public void ClearAll_KeepsLength()
        {
            var bits = FromIndices(100, 1, 99);
            bits.ClearAll();
            Assert.AreEqual(0, bits.Count);
            Assert.AreEqual(100, bits.Length);
            Assert.AreEqual(-1, bits.FirstSet());
        }

        [Test]
        public void SteadyState_ScanAndMaskOps_ZeroAlloc()
        {
            var bits = new BitSet(4096);
            var mask = new BitSet(4096);
            var entity = new BitSet(4096);
            for (int i = 0; i < 4096; i += 3) mask.Set(i);
            for (int i = 0; i < 4096; i += 7) entity.Set(i);
            long sink = 0;
            ZeroAlloc.Assert(() =>
            {
                for (int i = 0; i < 4096; i += 5) bits.Set(i);
                foreach (int index in bits) sink += index;
                for (int i = bits.FirstSet(); i >= 0; i = bits.NextSet(i + 1)) sink++;
                sink += bits.FirstClear();
                bits.And(mask);
                bits.Or(entity);
                if (entity.IsSupersetOf(mask)) sink--;
                if (entity.Intersects(mask)) sink++;
                bits.ClearRange(0, 4096);
            });
            Assert.Greater(sink, 0L);
        }

        private static BitSet FromIndices(int length, params int[] indices)
        {
            var bits = new BitSet(length);
            for (int i = 0; i < indices.Length; i++) bits.Set(indices[i]);
            return bits;
        }

        private static void AssertIndices(BitSet bits, params int[] expected)
        {
            Assert.AreEqual(expected.Length, bits.Count, "Count");
            int n = 0;
            foreach (int index in bits)
            {
                Assert.Less(n, expected.Length);
                Assert.AreEqual(expected[n++], index);
            }
        }

        private static void AssertMatches(BitSet bits, bool[] model)
        {
            int count = 0;
            int firstSet = -1;
            int firstClear = -1;
            for (int i = 0; i < model.Length; i++)
            {
                Assert.AreEqual(model[i], bits.Test(i), "bit " + i);
                if (model[i])
                {
                    count++;
                    if (firstSet < 0) firstSet = i;
                }
                else if (firstClear < 0)
                {
                    firstClear = i;
                }
            }

            Assert.AreEqual(count, bits.Count);
            Assert.AreEqual(firstSet, bits.FirstSet());
            Assert.AreEqual(firstClear, bits.FirstClear());
        }
    }
}
