using System;
using System.Threading;
using NUnit.Framework;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Tests.Ecs
{
    public class TickClockTests
    {
        [Test]
        public void DefaultClock_AccumulatesThenConsumesSequentialTicks()
        {
            var clock = new TickClock();
            Assert.That(clock.TickRate, Is.EqualTo(30));
            Assert.That(clock.MaxTicksPerFrame, Is.EqualTo(4));
            Assert.That(clock.MaxFrameDelta, Is.EqualTo(0.25));
            Assert.That(clock.TryAdvance(out var empty), Is.False);
            Assert.That(empty.Tick, Is.Zero);
            Assert.That(clock.Accumulate(2.5 / 30), Is.EqualTo(2));
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.PendingTicks, Is.EqualTo(2));
            Assert.That(clock.Alpha, Is.EqualTo(0.5).Within(1e-12));
            for (uint tick = 1; tick <= 2; tick++)
            {
                Assert.That(clock.TryAdvance(out var time), Is.True);
                Assert.That(time.Tick, Is.EqualTo(tick));
                Assert.That(time.DeltaTime, Is.EqualTo(1.0 / 30));
                Assert.That(time.TickInterval, Is.EqualTo(clock.TickInterval));
                Assert.That(time.ElapsedTime, Is.EqualTo(tick / 30.0));
                Assert.That(time.Alpha, Is.Zero);
                Assert.That(time.IsReplay, Is.False);
            }
            Assert.That(clock.Tick, Is.EqualTo(2));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.TryAdvance(out _), Is.False);
        }

        [Test]
        public void FractionalFrames_ProduceExactTickWithoutEarlyAdvance()
        {
            var clock = new TickClock(8);
            Assert.That(clock.Accumulate(0), Is.Zero);
            Assert.That(clock.Accumulate(0.0625), Is.Zero);
            Assert.That(clock.Alpha, Is.EqualTo(0.5));
            Assert.That(clock.Accumulate(0.0625), Is.EqualTo(1));
            Assert.That(clock.Alpha, Is.Zero);
            Assert.That(clock.TryAdvance(out _), Is.True);
        }

        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void Default30Hz_BinaryPartitions_AdvanceOnlyWhenComplete(int partitions)
        {
            var clock = new TickClock();
            double delta = clock.TickInterval / partitions;
            for (int part = 1; part < partitions; part++)
            {
                Assert.That(clock.Accumulate(delta), Is.Zero);
                Assert.That(clock.TryAdvance(out _), Is.False);
                Assert.That(clock.Tick, Is.Zero);
                Assert.That(clock.Alpha, Is.EqualTo((double)part / partitions));
            }
            Assert.That(clock.Accumulate(delta), Is.EqualTo(1));
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.Alpha, Is.Zero);
            Assert.That(clock.TryAdvance(out var time), Is.True);
            Assert.That(time.Tick, Is.EqualTo(1));
            Assert.That(clock.TryAdvance(out _), Is.False);
            Assert.That(clock.DroppedTicks, Is.Zero);
        }

        [Test]
        public void Default30Hz_TenthTickPartitions_DoNotRoundRemainderUp()
        {
            var clock = new TickClock();
            const double delta = 1.0 / 300;
            // Ten additions of the rounded 0.1 tick remain below one in double arithmetic.
            for (int part = 0; part < 10; part++)
            {
                Assert.That(clock.Accumulate(delta), Is.Zero);
                Assert.That(clock.TryAdvance(out _), Is.False);
            }
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.Alpha, Is.EqualTo(0.9999999999999999));
            Assert.That(clock.Accumulate(delta), Is.EqualTo(1));
            Assert.That(clock.TryAdvance(out var time), Is.True);
            Assert.That(time.Tick, Is.EqualTo(1));
            Assert.That(clock.Alpha, Is.GreaterThan(0).And.LessThan(0.1));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.DroppedTicks, Is.Zero);
        }

        [TestCase(-1, 0)]
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        public void Default30Hz_AdjacentBoundaryDoubles_UseStrictTickUnits(int bitOffset, int expectedTicks)
        {
            var clock = new TickClock();
            // Positive IEEE-754 doubles have consecutive bit patterns for adjacent values.
            long boundaryBits = BitConverter.DoubleToInt64Bits(clock.TickInterval);
            double delta = BitConverter.Int64BitsToDouble(boundaryBits + bitOffset);
            Assert.That(clock.Accumulate(delta), Is.EqualTo(expectedTicks));
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.PendingTicks, Is.EqualTo(expectedTicks));
            Assert.That(clock.Alpha, Is.EqualTo(delta * 30 - expectedTicks));
            if (bitOffset != 0) Assert.That(clock.Alpha, Is.GreaterThan(0).And.LessThan(1));
            Assert.That(clock.TryAdvance(out var time), Is.EqualTo(expectedTicks == 1));
            Assert.That(time.Tick, Is.EqualTo((uint)expectedTicks));
            Assert.That(clock.TryAdvance(out _), Is.False);
            Assert.That(clock.DroppedTicks, Is.Zero);
        }

        [Test]
        public void CatchUp_DropsExcessWholeTicksButRetainsFraction()
        {
            var clock = new TickClock(8, 2, 1);
            Assert.That(clock.Accumulate(0.9375), Is.EqualTo(2));
            Assert.That(clock.DroppedTicks, Is.EqualTo(5));
            Assert.That(clock.Alpha, Is.EqualTo(0.5));
            Assert.Throws<InvalidOperationException>(() => clock.Accumulate(0));
            Assert.That(clock.PendingTicks, Is.EqualTo(2));
            clock.TryAdvance(out _);
            clock.TryAdvance(out _);
            Assert.That(clock.Accumulate(0.0625), Is.EqualTo(1));
            Assert.That(clock.DroppedTicks, Is.EqualTo(5));
        }

        [Test]
        public void FrameClamp_IsAppliedBeforeCatchUpBudget()
        {
            var clock = new TickClock(8, 4, 0.25);
            Assert.That(clock.Accumulate(double.MaxValue), Is.EqualTo(2));
            Assert.That(clock.DroppedTicks, Is.Zero);
            Assert.That(clock.Alpha, Is.Zero);
        }

        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidDelta_LeavesStateUnchanged(double delta)
        {
            var clock = new TickClock(8);
            clock.Accumulate(0.0625);
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Accumulate(delta));
            Assert.That(clock.Alpha, Is.EqualTo(0.5));
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.DroppedTicks, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(1001)]
        public void InvalidRate_IsRejected(int rate)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new TickClock(rate));

        [TestCase(0)]
        [TestCase(-1)]
        public void InvalidBudget_IsRejected(int budget)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new TickClock(maxTicksPerFrame: budget));

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(double.MaxValue)]
        public void InvalidFrameLimit_IsRejected(double limit)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new TickClock(maxFrameDelta: limit));

        [Test]
        public void HugeAcceptedDelta_SaturatesDiagnosticsWithoutOverflow()
        {
            var clock = new TickClock(1, 1, double.MaxValue);
            Assert.That(clock.Accumulate(0.5), Is.Zero);
            for (int i = 0; i < 2; i++)
            {
                Assert.That(clock.Accumulate(double.MaxValue), Is.EqualTo(1));
                Assert.That(clock.DroppedTicks, Is.EqualTo(long.MaxValue));
                Assert.That(clock.Alpha, Is.EqualTo(0.5));
                Assert.That(clock.TryAdvance(out _), Is.True);
            }
        }

        [Test]
        public void Reset_ClearsBatchFractionAndDiagnostics_AndAllowsOverflowRecovery()
        {
            var clock = new TickClock(8, 1, 1);
            clock.Accumulate(0.9375);
            clock.Reset(uint.MaxValue - 1);
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.Alpha, Is.Zero);
            Assert.That(clock.DroppedTicks, Is.Zero);
            clock.Accumulate(0.125);
            Assert.That(clock.TryAdvance(out var last), Is.True);
            Assert.That(last.Tick, Is.EqualTo(uint.MaxValue));
            clock.Accumulate(0.125);
            Assert.Throws<OverflowException>(() => clock.TryAdvance(out _));
            Assert.That(clock.Tick, Is.EqualTo(uint.MaxValue));
            Assert.That(clock.PendingTicks, Is.EqualTo(1));
            clock.Reset();
            clock.Accumulate(0.125);
            clock.TryAdvance(out var first);
            Assert.That(first.Tick, Is.EqualTo(1));
        }

        [TestCase(1)]
        [TestCase(30)]
        [TestCase(1000)]
        public void TenThousandTicks_AreSequentialAndAllocateNothing(int rate)
        {
            var clock = new TickClock(rate, 4, 1);
            uint last = 0;
            int failedAdvances = 0;
            int unexpectedTicks = 0;
            int iterations = 1;
            TestDelegate body = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    clock.Accumulate(clock.TickInterval);
                    if (!clock.TryAdvance(out var time)) failedAdvances++;
                    if (time.Tick != (uint)(i + 1)) unexpectedTicks++;
                    last = time.Tick;
                }
            };
            for (int i = 0; i < 32; i++) { clock.Reset(); body(); }
            clock.Reset();
            failedAdvances = unexpectedTicks = 0;
            last = 0;
            iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(failedAdvances, Is.Zero);
            Assert.That(unexpectedTicks, Is.Zero);
            Assert.That(last, Is.EqualTo(10000));
            Assert.That(clock.Alpha, Is.Zero.Within(1e-9));
        }

        [Test]
        public void ForeignThread_MutationsAreRejected()
        {
            var clock = new TickClock();
            Exception accumulate = null, advance = null, reset = null;
            var thread = new Thread(() =>
            {
                try { clock.Accumulate(0); } catch (Exception ex) { accumulate = ex; }
                try { clock.TryAdvance(out _); } catch (Exception ex) { advance = ex; }
                try { clock.Reset(); } catch (Exception ex) { reset = ex; }
            });
            thread.Start();
            thread.Join();
            Assert.That(accumulate, Is.TypeOf<InvalidOperationException>());
            Assert.That(advance, Is.TypeOf<InvalidOperationException>());
            Assert.That(reset, Is.TypeOf<InvalidOperationException>());
            Assert.That(clock.Tick, Is.Zero);
        }
    }
}
