using System;
using NUnit.Framework;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Tests.Ecs
{
    public class EcsTimeTests
    {
        [TestCase(0.0)]
        [TestCase(0.123456789012345)]
        [TestCase(double.MaxValue)]
        public void ForFrame_PreservesDeltaWithoutInventingTickState(double delta)
        {
            var time = EcsTime.ForFrame(delta);
            Assert.That(time.DeltaTime, Is.EqualTo(delta));
            Assert.That(time.Tick, Is.Zero);
            Assert.That(time.TickInterval, Is.Zero);
            Assert.That(time.Alpha, Is.Zero);
            Assert.That(time.ElapsedTime, Is.Zero);
            Assert.That(time.IsReplay, Is.False);
        }

        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void ForFrame_InvalidDeltaIsRejected(double delta)
            => Assert.Throws<ArgumentOutOfRangeException>(() => EcsTime.ForFrame(delta));

        [TestCase(1u, 0.0, false)]
        [TestCase(0u, 0.5, false)]
        [TestCase(0u, 0.0, true)]
        public void ZeroInterval_RejectsSimulationState(uint tick, double alpha, bool replay)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new EcsTime(tick, 0, 0, alpha, replay));

        [Test]
        public void Snapshot_PreservesFrameAndReplayValues()
        {
            var time = new EcsTime(12, 0.01, 0.125, 0.5, true);
            Assert.That(time.Tick, Is.EqualTo(12));
            Assert.That(time.DeltaTime, Is.EqualTo(0.01));
            Assert.That(time.TickInterval, Is.EqualTo(0.125));
            Assert.That(time.Alpha, Is.EqualTo(0.5));
            Assert.That(time.ElapsedTime, Is.EqualTo(1.5));
            Assert.That(time.IsReplay, Is.True);
            Assert.That(default(EcsTime).Tick, Is.EqualTo(TickMath.Invalid));
        }

        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidDelta_IsRejected(double value)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new EcsTime(1, value, 0.125));

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidInterval_IsRejected(double value)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new EcsTime(1, 0, value));

        [TestCase(-1)]
        [TestCase(1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidAlpha_IsRejected(double value)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new EcsTime(1, 0, 0.125, value));

        [Test]
        public void ElapsedOverflow_IsRejected_AndZeroTickIsAllowedForInitialFrame()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EcsTime(2, 0, double.MaxValue));
            var initial = new EcsTime(0, 0, 0.125);
            Assert.That(initial.ElapsedTime, Is.Zero);
            Assert.That(initial.IsReplay, Is.False);
        }
    }

    public class TickMathTests
    {
        [TestCase(5u, 3u, 2)]
        [TestCase(3u, 5u, -2)]
        [TestCase(1u, uint.MaxValue, 2)]
        [TestCase(uint.MaxValue, 1u, -2)]
        [TestCase(0u, 0u, 0)]
        [TestCase(0x7fffffffu, 0u, int.MaxValue)]
        [TestCase(0x80000000u, 0u, int.MinValue)]
        public void ModularDelta_HasExplicitHalfRangeSemantics(uint a, uint b, int delta)
        {
            Assert.That(TickMath.Delta(a, b), Is.EqualTo(delta));
            Assert.That(TickMath.IsNewer(a, b), Is.EqualTo(delta > 0));
        }
    }
}
