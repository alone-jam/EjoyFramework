using System;
using EjoyFramework.GamePlay.ThreeC;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests
{
    public sealed class CharacterVelocitySolverTests
    {
        [Test] public void ProjectedAccelerationPreservesSidewaysMomentumUnlessLimited()
        {
            var velocity = new ThreeCVector3(6, 3, 0);
            var result = CharacterVelocitySolver.StepHorizontal(in velocity, 0, 1, 6, 30, 0, 1f / 60);
            Assert.That(result.X, Is.EqualTo(6)); Assert.That(result.Z, Is.EqualTo(3).Within(0.0001)); Assert.That(result.Y, Is.EqualTo(3));
            result = CharacterVelocitySolver.StepHorizontal(in velocity, 0, 1, 6, 30, 0, 1f / 60, true);
            Assert.That(result.HorizontalSqrMagnitude, Is.EqualTo(36).Within(0.0001));
        }
        [Test] public void FrictionStopsSmallVelocitiesWithoutReversingAndAnalogTargetDoesNotOvershoot()
        {
            var velocity = new ThreeCVector3(0.01f, 2, 0);
            var stopped = CharacterVelocitySolver.StepHorizontal(in velocity, 0, 0, 6, 30, 16, 1f / 60);
            Assert.That(stopped.X, Is.Zero); Assert.That(stopped.Y, Is.EqualTo(2));
            var result = CharacterVelocitySolver.StepHorizontal(in stopped, 0, 0.1f, 6, 30, 16, 1f / 60);
            Assert.That(result.Z, Is.EqualTo(0.6f).Within(0.0001));
        }
        [Test] public void ZeroDurationIsIdentityAndGravitySaturates()
        {
            var v = new ThreeCVector3(3, 4, 5);
            Assert.That(CharacterVelocitySolver.StepHorizontal(in v, 1, 1, 6, 30, 16, 0), Is.EqualTo(v));
            Assert.That(CharacterVelocitySolver.ApplyGravity(-49, 18, 50, 1), Is.EqualTo(-50));
            Assert.That(CharacterVelocitySolver.ApplyGravity(4, 18, 50, 0), Is.EqualTo(4));
        }
        [Test] public void RestoredStateReplaysExactlyWithoutHiddenSolverState()
        {
            var saved = new ThreeCVector3(1, 2, 3); var first = saved; var second = saved;
            for (int i = 0; i < 100; i++) first = CharacterVelocitySolver.StepHorizontal(in first, 0.5f, -0.4f, 6, 1, 0.5f, 1f / 60);
            for (int i = 0; i < 100; i++) second = CharacterVelocitySolver.StepHorizontal(in second, 0.5f, -0.4f, 6, 1, 0.5f, 1f / 60);
            Assert.That(second, Is.EqualTo(first));
        }
        [Test] public void InvalidInputFailsBeforeProducingNonFiniteState()
        {
            var v = default(ThreeCVector3);
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterVelocitySolver.StepHorizontal(in v, float.NaN, 0, 6, 1, 1, 0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterVelocitySolver.StepHorizontal(in v, 0, 0, 6, 1, 1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterVelocitySolver.ApplyGravity(0, float.PositiveInfinity, 50, 1));
        }
    }
}
