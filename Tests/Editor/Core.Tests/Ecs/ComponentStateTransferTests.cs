using System;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class ComponentStateTransferTests
    {
        private struct State { public int Value; }
        private struct Velocity { public int Value; }

        private static Entity Spawn(World world, int value)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new State { Value = value });
            return entity;
        }

        [Test]
        public void Capture_ReturnsIndependentValueAndMembershipIdentity()
        {
            using var world = new World();
            var entity = Spawn(world, 3);
            Assert.That(ComponentStateTransfer.TryCapture<State>(world, entity, out var lifetime, out var captured), Is.True);
            Assert.That(world.TryGetComponentLifetime<State>(entity, out var current), Is.True);
            Assert.That(lifetime, Is.EqualTo(current));
            world.Set(entity, new State { Value = 8 });
            Assert.That(captured.Value, Is.EqualTo(3));
            world.Query().ForEach(e =>
                Assert.That(ComponentStateTransfer.TryCapture<State>(world, e, out _, out _), Is.True));
        }

        [Test]
        public void Capture_RejectsUnavailableTargetsWithDefaultOutputs()
        {
            using var world = new World();
            using var foreign = new World();
            var missing = world.CreateEntity();
            var stale = Spawn(world, 1);
            world.DestroyEntity(stale);
            var pending = Spawn(world, 2);
            world.RequestDespawn(pending);
            foreach (var entity in new[] { default(Entity), missing, stale, pending, Spawn(foreign, 4) })
            {
                Assert.That(ComponentStateTransfer.TryCapture<State>(world, entity, out var lifetime, out var value), Is.False);
                Assert.That(lifetime, Is.Zero);
                Assert.That(value.Value, Is.Zero);
            }
        }

        [Test]
        public void Restore_RequiresExactTickAndPreservesLifetimeAndSourceBaseline()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var binding = source.Bind(entity);
            var value = new State { Value = 7 };
            source.TrySet(in binding, 10, in value);
            world.TryGetComponentLifetime<State>(entity, out var lifetime);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 9), Is.False);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 11), Is.False);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ComponentStateTransfer.TryRestoreExact(world, source, in binding, 0));
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 10), Is.True);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(7));
            world.TryGetComponentLifetime<State>(entity, out var after);
            Assert.That(after, Is.EqualTo(lifetime));
            Assert.That(source.TryGet(in binding, out var tick, out var original), Is.True);
            Assert.That(tick, Is.EqualTo(10));
            Assert.That(original.Value, Is.EqualTo(7));
        }

        [Test]
        public void Restore_DoesNotWriteForeignWorldOrAddMissingComponents()
        {
            using var world = new World();
            using var foreign = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 1);
            var other = Spawn(foreign, 2);
            var binding = source.Bind(entity);
            var baseline = new State { Value = 50 };
            source.TrySet(in binding, 3, in baseline);
            Assert.That(ComponentStateTransfer.TryRestoreExact(foreign, source, in binding, 3), Is.False);
            Assert.That(foreign.Get<State>(other).Value, Is.EqualTo(2));
            world.Remove<State>(entity);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 3), Is.False);
            Assert.That(world.Has<State>(entity), Is.False);
            world.Set(entity, new State { Value = 20 });
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 3), Is.False);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(20));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Restore_RejectsTimelineResetOrDespawningTarget(bool reset)
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var binding = source.Bind(entity);
            var value = new State { Value = 7 };
            source.TrySet(in binding, 10, in value);
            if (reset) source.ResetTimeline();
            else world.RequestDespawn(entity);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 10), Is.False);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
        }

        [Test]
        public void Restore_RejectsRecycledEntityIndex()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var old = Spawn(world, 1);
            var binding = source.Bind(old);
            var baseline = new State { Value = 7 };
            source.TrySet(in binding, 1, in baseline);
            world.DestroyEntity(old);
            var current = Spawn(world, 2);
            Assert.That(current.Index, Is.EqualTo(old.Index));
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 1), Is.False);
            Assert.That(world.Get<State>(current).Value, Is.EqualTo(2));
        }

        [Test]
        public void Restore_RejectsIterationWithoutMutatingWorld()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var binding = source.Bind(entity);
            var value = new State { Value = 7 };
            source.TrySet(in binding, 1, in value);
            world.Query().ForEach(e => Assert.Throws<InvalidOperationException>(() =>
                ComponentStateTransfer.TryRestoreExact(world, source, in binding, 1)));
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in binding, 1), Is.True);
        }

        [Test]
        public void InvalidLifetimesAndNullInputs_ThrowExplicitErrors()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 1);
            var binding = source.Bind(entity);
            Assert.Throws<ArgumentNullException>(() => ComponentStateTransfer.TryCapture<State>(null, entity, out _, out _));
            Assert.Throws<ArgumentNullException>(() => ComponentStateTransfer.TryRestoreExact(null, source, in binding, 1));
            Assert.Throws<ArgumentNullException>(() => ComponentStateTransfer.TryRestoreExact<State>(world, null, in binding, 1));
            source.Dispose();
            Assert.Throws<ObjectDisposedException>(() => ComponentStateTransfer.TryRestoreExact(world, source, in binding, 1));
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => ComponentStateTransfer.TryCapture<State>(world, entity, out _, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PairRestore_InvalidSecondBaselineLeavesFirstComponentUnchanged(bool replaceMembership)
        {
            using var world = new World();
            using var positions = new AuthoritativeBaselineStore<State>(world);
            using var velocities = new AuthoritativeBaselineStore<Velocity>(world);
            var entity = Spawn(world, 99);
            world.Set(entity, new Velocity { Value = 88 });
            var position = positions.Bind(entity);
            var velocity = velocities.Bind(entity);
            var p = new State { Value = 7 };
            var v = new Velocity { Value = 8 };
            positions.TrySet(in position, 10, in p);
            velocities.TrySet(in velocity, replaceMembership ? 10u : 9u, in v);
            if (replaceMembership)
            {
                world.Remove<Velocity>(entity);
                world.Set(entity, new Velocity { Value = 88 });
            }
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, positions, in position, velocities, in velocity, 10), Is.False);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
            Assert.That(world.Get<Velocity>(entity).Value, Is.EqualTo(88));
        }

        [Test]
        public void PairRestore_ValidatesBothAndPreservesMemberships()
        {
            using var world = new World();
            using var positions = new AuthoritativeBaselineStore<State>(world);
            using var velocities = new AuthoritativeBaselineStore<Velocity>(world);
            var entity = Spawn(world, 99);
            world.Set(entity, new Velocity { Value = 88 });
            var position = positions.Bind(entity);
            var velocity = velocities.Bind(entity);
            var p = new State { Value = 7 };
            var v = new Velocity { Value = 8 };
            positions.TrySet(in position, 10, in p);
            velocities.TrySet(in velocity, 10, in v);
            world.TryGetComponentLifetime<State>(entity, out ulong positionLifetime);
            world.TryGetComponentLifetime<Velocity>(entity, out ulong velocityLifetime);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, positions, in position, velocities, in velocity, 10), Is.True);
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(7));
            Assert.That(world.Get<Velocity>(entity).Value, Is.EqualTo(8));
            world.TryGetComponentLifetime<State>(entity, out ulong afterPosition);
            world.TryGetComponentLifetime<Velocity>(entity, out ulong afterVelocity);
            Assert.That(afterPosition, Is.EqualTo(positionLifetime));
            Assert.That(afterVelocity, Is.EqualTo(velocityLifetime));
            world.Query().ForEach(e => Assert.Throws<InvalidOperationException>(() =>
                ComponentStateTransfer.TryRestoreExact(world, positions, in position, velocities, in velocity, 10)));
        }

        [Test]
        public void PairRestore_RejectsTwoBaselinesTargetingSameMembership()
        {
            using var world = new World();
            using var first = new AuthoritativeBaselineStore<State>(world);
            using var second = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var a = first.Bind(entity);
            var b = second.Bind(entity);
            var one = new State { Value = 1 };
            var two = new State { Value = 2 };
            first.TrySet(in a, 1, in one);
            second.TrySet(in b, 1, in two);
            Assert.Throws<ArgumentException>(() => ComponentStateTransfer.TryRestoreExact(world, first, in a, second, in b, 1));
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
        }

        [Test]
        public void PairRestore_AllowsSameTypeOnDistinctEntities()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var first = Spawn(world, 99);
            var second = Spawn(world, 88);
            var a = source.Bind(first);
            var b = source.Bind(second);
            var one = new State { Value = 1 };
            var two = new State { Value = 2 };
            source.TrySet(in a, 1, in one);
            source.TrySet(in b, 1, in two);
            Assert.That(ComponentStateTransfer.TryRestoreExact(world, source, in a, source, in b, 1), Is.True);
            Assert.That(world.Get<State>(first).Value, Is.EqualTo(1));
            Assert.That(world.Get<State>(second).Value, Is.EqualTo(2));
        }

        [Test]
        public void WarmPairRestore_AllocatesNothing()
        {
            using var world = new World();
            using var positions = new AuthoritativeBaselineStore<State>(world);
            using var velocities = new AuthoritativeBaselineStore<Velocity>(world);
            var entity = Spawn(world, 99);
            world.Set(entity, new Velocity { Value = 88 });
            var position = positions.Bind(entity);
            var velocity = velocities.Bind(entity);
            var p = new State { Value = 7 };
            var v = new Velocity { Value = 8 };
            positions.TrySet(in position, 1, in p);
            velocities.TrySet(in velocity, 1, in v);
            int restored = 0;
            TestDelegate body = () =>
            {
                restored = 0;
                for (int i = 0; i < 10000; i++)
                    if (ComponentStateTransfer.TryRestoreExact(world, positions, in position, velocities, in velocity, 1)) restored++;
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(restored, Is.EqualTo(10000));
        }

        [Test]
        public void WarmCaptureAndExactRestore_AllocateNothing()
        {
            using var world = new World();
            using var source = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var binding = source.Bind(entity);
            var value = new State { Value = 7 };
            source.TrySet(in binding, 10, in value);
            int restored = 0, captured = 0;
            long sum = 0;
            TestDelegate body = () =>
            {
                restored = captured = 0;
                sum = 0;
                for (int i = 0; i < 10000; i++)
                {
                    if (ComponentStateTransfer.TryRestoreExact(world, source, in binding, 10)) restored++;
                    if (ComponentStateTransfer.TryCapture<State>(world, entity, out _, out var copy)) { captured++; sum += copy.Value; }
                }
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(restored, Is.EqualTo(10000));
            Assert.That(captured, Is.EqualTo(10000));
            Assert.That(sum, Is.EqualTo(70000));
        }
    }
}
