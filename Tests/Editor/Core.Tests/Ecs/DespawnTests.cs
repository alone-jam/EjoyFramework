using System;
using System.Collections.Generic;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class DespawnTests
    {
        [Test]
        public void Request_IsIdempotentAndNonStructural_WhileComponentsRemainReadable()
        {
            using var world = new World();
            var entity = world.CreateEntity();
            world.Set(entity, new Health { Value = 9 });
            var query = world.Query();
            query.ForEach(e =>
            {
                Assert.That(world.RequestDespawn(e), Is.True);
                Assert.That(world.RequestDespawn(e), Is.False);
                Assert.That(query.Count, Is.Zero);
                Assert.That(world.IsAlive(e), Is.True);
                Assert.That(world.IsDespawning(e), Is.True);
                Assert.That(world.Get<Health>(e).Value, Is.EqualTo(9));
                Assert.Throws<InvalidOperationException>(() => world.ProcessDespawns());
            });
            Assert.That(world.EntityCount, Is.EqualTo(1));
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(world.ProcessDespawns(), Is.Zero);
            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(world.PendingDespawnCount, Is.Zero);
        }

        [Test]
        public void Requests_IgnoreStaleForeignDefaultAndReservedHandles_ButRejectDisposedWorld()
        {
            var world = new World();
            using var foreign = new World();
            using var commands = new CommandBuffer(world);
            var entity = world.CreateEntity();
            world.DestroyEntity(entity);
            Assert.That(world.RequestDespawn(entity), Is.False);
            Assert.That(world.RequestDespawn(foreign.CreateEntity()), Is.False);
            Assert.That(world.RequestDespawn(default), Is.False);
            Assert.That(world.RequestDespawn(commands.CreateEntity()), Is.False);
            Assert.That(world.PendingDespawnCount, Is.Zero);
            var live = world.CreateEntity();
            world.RequestDespawn(live);
            world.Dispose();
            Assert.That(world.PendingDespawnCount, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => world.RequestDespawn(live));
            Assert.Throws<ObjectDisposedException>(() => world.IsDespawning(live));
            Assert.Throws<ObjectDisposedException>(() => world.ProcessDespawns());
            Assert.DoesNotThrow(() => commands.Dispose());
        }

        [Test]
        public void ImmediateDestroyAndSlotReuse_DoNotRetargetQueuedDespawn()
        {
            using var world = new World(1);
            var first = world.CreateEntity();
            world.RequestDespawn(first);
            world.DestroyEntity(first);
            var second = world.CreateEntity();
            Assert.That(second.Index, Is.EqualTo(first.Index));
            Assert.That(world.IsDespawning(second), Is.False);
            Assert.That(world.PendingDespawnCount, Is.Zero);
            Assert.That(world.ProcessDespawns(), Is.Zero);
            Assert.That(world.IsAlive(second), Is.True);
            world.RequestDespawn(second);
            world.DestroyEntity(second);
            var third = world.CreateEntity();
            world.RequestDespawn(third);
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(world.IsAlive(third), Is.False);
        }

        [Test]
        public void QueryVariants_AreImmutableAndRetainDespawnModeThroughAllFiltersAndTypedArities()
        {
            using var world = new World();
            var live = world.CreateEntity();
            var dying = world.CreateEntity();
            foreach (var entity in new[] { live, dying })
            {
                world.Set(entity, new Health()); world.Set(entity, new Position());
                world.Set(entity, new Velocity()); world.Set(entity, new Disabled());
            }
            var original = world.Query();
            var include = original.IncludeDespawning().WithAll<Health>().WithAny<Position>().WithNone<Missing>();
            var only = original.OnlyDespawning().WithAll<Health>().WithAny<Position>().WithNone<Missing>();
            world.RequestDespawn(dying);
            Assert.That(original.Count, Is.EqualTo(1));
            Assert.That(include.Count, Is.EqualTo(2));
            Assert.That(only.Count, Is.EqualTo(1));
            Assert.That(include.OnlyDespawning().Count, Is.EqualTo(1));
            Assert.That(only.IncludeDespawning().Count, Is.EqualTo(2));
            int visited = 0;
            only.ForEach<Health>((Entity e, ref Health h) => { Assert.That(e, Is.EqualTo(dying)); visited++; });
            only.ForEach<Health, Position>((Entity e, ref Health h, ref Position p) => { Assert.That(e, Is.EqualTo(dying)); visited++; });
            only.ForEach<Health, Position, Velocity>((Entity e, ref Health h, ref Position p, ref Velocity v) =>
                { Assert.That(e, Is.EqualTo(dying)); visited++; });
            only.ForEach<Health, Position, Velocity, Disabled>((Entity e, ref Health h, ref Position p, ref Velocity v, ref Disabled d) =>
                { Assert.That(e, Is.EqualTo(dying)); visited++; });
            Assert.That(visited, Is.EqualTo(4));
            var included = new List<Entity>();
            include.ForEach<Health>((Entity e, ref Health h) => included.Add(e));
            Assert.That(included, Is.EquivalentTo(new[] { live, dying }));
            world.ProcessDespawns();
            Assert.That(only.Count, Is.Zero);
            Assert.That(include.Count, Is.EqualTo(1));
        }

        [Test]
        public void DefaultQuery_SkipsEntitiesRequestedEarlierInTheSameIteration()
        {
            using var world = new World();
            var first = world.CreateEntity();
            var second = world.CreateEntity();
            var visited = new List<Entity>();
            world.Query().ForEach(e => { visited.Add(e); world.RequestDespawn(second); });
            Assert.That(visited, Is.EqualTo(new[] { first }));
            Assert.That(world.IsAlive(second), Is.True);
        }

        [Test]
        public void DuplicateCommandDestroy_IsIdempotent_AndLaterComponentWritesAreStillValid()
        {
            using var world = new World();
            using var commands = new CommandBuffer(world);
            var entity = commands.CreateEntity();
            commands.DestroyEntity(entity);
            commands.DestroyEntity(entity);
            commands.Set(entity, new Health { Value = 42 });
            Assert.DoesNotThrow(() => commands.Playback());
            Assert.That(world.IsAlive(entity), Is.True);
            Assert.That(world.Get<Health>(entity).Value, Is.EqualTo(42));
            Assert.That(world.Query().Count, Is.Zero);
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
        }

        [Test]
        public void StaleSetAndRemove_StillFailPlayback_WithoutAffectingReusedEntity()
        {
            using var world = new World();
            using var set = new CommandBuffer(world);
            using var remove = new CommandBuffer(world);
            var old = world.CreateEntity();
            set.Set(old, new Health());
            remove.Remove<Health>(old);
            world.DestroyEntity(old);
            var next = world.CreateEntity();
            Assert.Throws<InvalidOperationException>(() => set.Playback());
            Assert.Throws<InvalidOperationException>(() => remove.Playback());
            Assert.That(world.IsAlive(next), Is.True);
        }

        [Test]
        public void WarmRequestQueryAndProcess_HaveNoManagedAllocations()
        {
            using var world = new World(1);
            var query = world.Query().OnlyDespawning();
            int observed = 0;
            int iterations = 1;
            TestDelegate body = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    var entity = world.CreateEntity();
                    world.RequestDespawn(entity);
                    observed += query.Count;
                    world.ProcessDespawns();
                }
            };
            for (int i = 0; i < 32; i++) body();
            observed = 0;
            iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(observed, Is.EqualTo(10000));
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.PendingDespawnCount, Is.Zero);
        }

        private struct Missing { }
    }
}
