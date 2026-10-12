using System;
using System.Collections.Generic;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class DespawnLoopTests
    {
        private sealed class CallbackSystem : ISystem
        {
            public Action<World, CommandBuffer, EcsTime> Callback;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => Callback(world, commands, time);
        }

        [Test]
        public void StandaloneGroup_FlushesOnlyAfterAllSystemsSucceed()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            var entity = world.CreateEntity();
            group.Add(new CallbackSystem { Callback = (w, c, t) => { c.DestroyEntity(entity); c.DestroyEntity(entity); } });
            group.Add(new CallbackSystem { Callback = (w, c, t) =>
            {
                Assert.That(w.IsAlive(entity), Is.True);
                Assert.That(w.Query().Count, Is.Zero);
                Assert.That(w.Query().OnlyDespawning().Count, Is.EqualTo(1));
            } });
            group.Update(0f);
            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(world.PendingDespawnCount, Is.Zero);
        }

        [Test]
        public void StandaloneGroupFailure_PreservesAppliedDespawnRequest()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            var entity = world.CreateEntity();
            var request = new CallbackSystem { Callback = (w, c, t) => c.DestroyEntity(entity) };
            var fail = new CallbackSystem { Callback = (w, c, t) => throw new ApplicationException() };
            group.Add(request); group.Add(fail);
            Assert.Throws<ApplicationException>(() => group.Update(0f));
            Assert.That(world.IsAlive(entity), Is.True);
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            group.Remove(request); group.Remove(fail);
            group.Update(0f);
            Assert.That(world.IsAlive(entity), Is.False);
        }

        [Test]
        public void Loop_DespawnRunsAfterTickAndAfterPresentation_ForNewFrameRequests()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            var first = world.CreateEntity();
            var second = world.CreateEntity();
            var only = world.Query().OnlyDespawning();
            var cleaned = new List<Entity>();
            var times = new List<EcsTime>();
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => c.DestroyEntity(first) });
            loop.Add(EcsPhase.TickResolve, new CallbackSystem { Callback = (w, c, t) =>
            {
                Assert.That(w.IsAlive(first), Is.True);
                Assert.That(only.Count, Is.EqualTo(1));
            } });
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) =>
            {
                only.ForEach(e => cleaned.Add(e)); times.Add(t);
                Assert.Throws<InvalidOperationException>(() => w.ProcessDespawns());
            } });
            loop.Add(EcsPhase.Presentation, new CallbackSystem { Callback = (w, c, t) =>
            {
                Assert.That(w.IsAlive(first), Is.False);
                c.DestroyEntity(second);
            } });
            Assert.That(loop.RunFrame(1.5 / 30), Is.EqualTo(1));
            Assert.That(cleaned, Is.EqualTo(new[] { first, second }));
            Assert.That(times.Count, Is.EqualTo(2));
            Assert.That(times[0].Tick, Is.EqualTo(1));
            Assert.That(times[0].Alpha, Is.Zero);
            Assert.That(times[0].DeltaTime, Is.EqualTo(1.0 / 30));
            Assert.That(times[1].Tick, Is.EqualTo(1));
            Assert.That(times[1].Alpha, Is.EqualTo(0.5));
            Assert.That(times[1].DeltaTime, Is.EqualTo(1.5 / 30));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void CleanupRequests_AreExcludedFromCurrentBatch_AndReceiveTheirOwnPass()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            var first = world.CreateEntity();
            var child = world.CreateEntity();
            world.RequestDespawn(first);
            var only = world.Query().OnlyDespawning();
            var firstSystem = new List<Entity>();
            var secondSystem = new List<Entity>();
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) =>
                only.ForEach(e => { firstSystem.Add(e); if (e == first) w.RequestDespawn(child); }) });
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) =>
                only.ForEach(e => secondSystem.Add(e)) });
            Assert.That(loop.RunFrame(0), Is.Zero);
            Assert.That(firstSystem, Is.EqualTo(new[] { first, child }));
            Assert.That(secondSystem, Is.EqualTo(new[] { first, child }));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void CascadingCleanup_IsBoundedToFourPassesPerSync_AndRemainderSurvives()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            var chain = new Entity[6];
            for (int i = 0; i < chain.Length; i++) chain[i] = world.CreateEntity();
            for (int i = 0; i < chain.Length - 1; i++) world.Set(chain[i], new Next { Entity = chain[i + 1] });
            var only = world.Query().OnlyDespawning();
            var cleaned = new List<Entity>();
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) => only.ForEach(e =>
            {
                cleaned.Add(e);
                if (w.TryGet(e, out Next next)) c.DestroyEntity(next.Entity);
            }) });
            world.RequestDespawn(chain[0]);
            loop.RunFrame(0);
            Assert.That(cleaned, Is.EqualTo(new[] { chain[0], chain[1], chain[2], chain[3] }));
            Assert.That(world.EntityCount, Is.EqualTo(2));
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.IsDespawning(chain[4]), Is.True);
            Assert.That(world.IsDespawning(chain[5]), Is.False);
            loop.RunFrame(0);
            Assert.That(cleaned, Is.EqualTo(chain));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void CleanupFailure_RestoresBatchAheadOfNewRequests_WithoutLosingHandles()
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            var first = world.CreateEntity();
            var child = world.CreateEntity();
            var only = world.Query().OnlyDespawning();
            world.RequestDespawn(first);
            var fail = new CallbackSystem { Callback = (w, c, t) =>
            {
                Assert.That(only.Count, Is.EqualTo(1));
                w.RequestDespawn(child);
                Assert.That(only.Count, Is.EqualTo(1));
                throw new ApplicationException();
            } };
            loop.Add(EcsPhase.Despawn, fail);
            Assert.Throws<ApplicationException>(() => loop.RunFrame(0));
            Assert.That(loop.IsFaulted, Is.True);
            Assert.That(world.PendingDespawnCount, Is.EqualTo(2));
            Assert.That(only.Count, Is.EqualTo(2)); // Batch restriction was released on failure.
            Assert.That(world.IsAlive(first), Is.True);
            Assert.That(world.IsAlive(child), Is.True);
            loop.Remove(EcsPhase.Despawn, fail);
            var cleaned = new List<Entity>();
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) => only.ForEach(e => cleaned.Add(e)) });
            loop.ResetClock(clock.Tick);
            Assert.That(world.PendingDespawnCount, Is.EqualTo(2));
            loop.RunFrame(0);
            Assert.That(cleaned, Is.EqualTo(new[] { first, child }));
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.CreateEntity().Index, Is.EqualTo(child.Index)); // FIFO processing, free-list is LIFO.
        }

        [Test]
        public void DisposingLoop_DoesNotDiscardWorldPendingDespawn()
        {
            using var world = new World();
            var loop = new EcsLoop(world, new TickClock());
            var entity = world.CreateEntity();
            world.RequestDespawn(entity);
            loop.Dispose();
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.IsAlive(entity), Is.True);
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
        }

        [Test]
        public void WarmLoopCleanup_IsAllocationFree()
        {
            using var world = new World(1);
            using var loop = new EcsLoop(world, new TickClock());
            var only = world.Query().OnlyDespawning();
            int cleanups = 0;
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) => cleanups += only.Count });
            int iterations = 1;
            TestDelegate body = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    world.RequestDespawn(world.CreateEntity()); loop.RunFrame(0);
                }
            };
            for (int i = 0; i < 32; i++) body();
            cleanups = 0;
            iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(cleanups, Is.EqualTo(10000));
            Assert.That(world.PendingDespawnCount, Is.Zero);
            Assert.That(world.EntityCount, Is.Zero);
        }

        private struct Next { public Entity Entity; }
    }
}
