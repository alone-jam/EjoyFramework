using System;
using NUnit.Framework;
using EjoyFramework.Core.Ecs;
using EjoyGame.Samples.EcsDemo;

namespace EjoyFramework.EcsSampleTests
{
    public class ScenarioTests
    {
        [TestCase(0f, 5f, 2f)]
        [TestCase(5f, 0f, -2f)]
        public void Projectile_MovesHitsDamagesAndDestroysBothEntities(float start, float targetX, float speed)
        {
            using var world = new World();
            using var systems = new SystemGroup(world);
            var scenario = new ProjectileScenario(world, systems);
            var target = scenario.SpawnTarget(targetX, 10);
            var shot = scenario.Fire(start, target, speed, 10);
            systems.Update(EcsTime.ForFrame(1));
            Assert.That(world.Get<Position>(shot).X, Is.EqualTo(start + speed));
            Assert.That(world.Get<Health>(target).Value, Is.EqualTo(10));
            systems.Update(EcsTime.ForFrame(2)); // Overshoots target; hit still occurs.
            Assert.That(world.IsAlive(shot), Is.False);
            Assert.That(world.IsAlive(target), Is.False);
            Assert.That(world.EntityCount, Is.Zero);
        }

        [TestCase(0f, 5f, 2f)]
        [TestCase(5f, 0f, -2f)]
        public void Projectile_PhasedLoopMovesResolvesAndDestroys(float start, float targetX, float speed)
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            var scenario = new ProjectileScenario(world, loop);
            var target = scenario.SpawnTarget(targetX, 10);
            var shot = scenario.Fire(start, target, speed, 10);
            for (int i = 0; i < 30; i++) loop.RunFrame(clock.TickInterval);
            Assert.That(world.Get<Position>(shot).X, Is.EqualTo(start + speed).Within(0.0001f));
            Assert.That(world.Get<Health>(target).Value, Is.EqualTo(10));
            for (int i = 0; i < 60; i++) loop.RunFrame(clock.TickInterval);
            Assert.That(world.IsAlive(shot), Is.False);
            Assert.That(world.IsAlive(target), Is.False);
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void TargetDestroyedBeforeImpact_ProjectileIsCleanedUp()
        {
            using var world = new World();
            using var systems = new SystemGroup(world);
            var scenario = new ProjectileScenario(world, systems);
            var target = scenario.SpawnTarget(5, 10);
            var shot = scenario.Fire(0, target, 1, 10);
            world.DestroyEntity(target);
            var replacement = scenario.SpawnTarget(5, 20);
            systems.Update(EcsTime.ForFrame(1));
            Assert.That(world.IsAlive(shot), Is.False);
            Assert.That(world.Get<Health>(replacement).Value, Is.EqualTo(20));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Movement_RejectsDeltaBeyondFloatRangeBeforeChangingAnyPosition(bool useDoubleMaximum)
        {
            using var world = new World();
            using var systems = new SystemGroup(world);
            var stationary = world.CreateEntity();
            world.Set(stationary, new Position { X = 7 });
            world.Set(stationary, new Velocity { X = 0 });
            var moving = world.CreateEntity();
            world.Set(moving, new Position { X = -3 });
            world.Set(moving, new Velocity { X = 2 });
            systems.Add(new MovementSystem(world));
            double delta = useDoubleMaximum ? double.MaxValue
                : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits((double)float.MaxValue) + 1);
            var time = EcsTime.ForFrame(delta); // Valid Core time, unsupported by this float-based sample.

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => systems.Update(in time));
            Assert.That(error.ParamName, Is.EqualTo("time"));
            Assert.That(world.Get<Position>(stationary).X, Is.EqualTo(7f));
            Assert.That(world.Get<Position>(moving).X, Is.EqualTo(-3f));
            Assert.That(world.EntityCount, Is.EqualTo(2));

            systems.Update(EcsTime.ForFrame(0.5));
            Assert.That(world.Get<Position>(stationary).X, Is.EqualTo(7f));
            Assert.That(world.Get<Position>(moving).X, Is.EqualTo(-2f));
        }

        [TestCase(0f)]
        [TestCase(1f)]
        public void Movement_AcceptsFloatMaximumDeltaAtTheBoundary(float velocity)
        {
            using var world = new World();
            using var systems = new SystemGroup(world);
            var entity = world.CreateEntity();
            world.Set(entity, new Position());
            world.Set(entity, new Velocity { X = velocity });
            systems.Add(new MovementSystem(world));
            systems.Update(EcsTime.ForFrame(float.MaxValue));
            Assert.That(world.Get<Position>(entity).X, Is.EqualTo(velocity == 0 ? 0f : float.MaxValue));
        }

        [Test]
        public void WarmMovement_HasNoPerTickManagedAllocations()
        {
            using var world = new World();
            using var systems = new SystemGroup(world);
            for (int i = 0; i < 10000; i++)
            {
                var entity = world.CreateEntity();
                world.Set(entity, new Position());
                world.Set(entity, new Velocity { X = 1 });
            }
            systems.Add(new MovementSystem(world));
            var time = EcsTime.ForFrame(0.5);
            for (int i = 0; i < 30; i++) systems.Update(in time);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) systems.Update(in time);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
            world.Query().ForEach<Position>((Entity e, ref Position p) => Assert.That(p.X, Is.EqualTo(65f)));
        }
    }
}
