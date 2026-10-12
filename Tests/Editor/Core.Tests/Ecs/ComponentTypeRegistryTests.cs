using System;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.Tests.Ecs
{
    public class ComponentTypeRegistryTests
    {
        private struct Alpha { public int Value; public object Reference; }
        private struct Beta { public long Value; }
        private struct AbsentA { }
        private struct AbsentB { }
        private struct ThreadValue<T> where T : struct { public int Value; }
        private struct SharedRace { public int Value; }
        private struct M0 { }
        private struct M1 { }
        private struct M2 { }
        private struct M3 { }
        private struct M4 { }
        private struct M5 { }
        private struct M6 { }
        private struct M7 { }

        [Test]
        public void SameTypesInDifferentFirstUseOrders_HaveIsolatedWorldContents()
        {
            using var first = new World(1);
            using var second = new World(1);
            var a = first.CreateEntity();
            var b = second.CreateEntity();
            var markerA = new object(); var markerB = new object();
            first.Set(a, new Alpha { Value = 11, Reference = markerA });
            first.Set(a, new Beta { Value = 12 });
            second.Set(b, new Beta { Value = 22 });
            second.Set(b, new Alpha { Value = 21, Reference = markerB });
            Assert.That(first.Get<Alpha>(a).Value, Is.EqualTo(11));
            Assert.That(first.Get<Beta>(a).Value, Is.EqualTo(12));
            Assert.That(second.Get<Alpha>(b).Reference, Is.SameAs(markerB));
            Assert.That(first.Get<Alpha>(a).Reference, Is.SameAs(markerA));
            first.Get<Alpha>(a).Value = 99;
            Assert.That(second.Get<Alpha>(b).Value, Is.EqualTo(21));
            Assert.That(first.Remove<Beta>(a), Is.True);
            Assert.That(second.Get<Beta>(b).Value, Is.EqualTo(22));
            Assert.That(first.Has<Alpha>(b), Is.False);
            Assert.That(second.TryGet(a, out Alpha _), Is.False);
            first.Dispose();
            Assert.That(second.Get<Alpha>(b).Value, Is.EqualTo(21));
            Assert.That(second.Get<Beta>(b).Value, Is.EqualTo(22));
        }

        [Test]
        public void AbsentProbes_OnFreshWorld_DoNotAllocatePoolsOrGrowItsTable()
        {
            using var warm = new World(1);
            using var target = new World(1);
            var warmEntity = warm.CreateEntity();
            var targetEntity = target.CreateEntity();
            World active = warm;
            Entity entity = warmEntity;
            int absent = 0;
            TestDelegate probe = () =>
            {
                absent = 0;
                if (!active.Has<AbsentA>(entity)) absent++;
                if (!active.Has<AbsentB>(entity)) absent++;
                if (!active.TryGet(entity, out AbsentA _)) absent++;
                if (!active.TryGet(entity, out AbsentB _)) absent++;
                if (!active.Remove<AbsentA>(entity)) absent++;
                if (!active.Remove<AbsentB>(entity)) absent++;
            };
            probe(); // Warm generic IDs and code, but never probe the target before measuring.
            active = target; entity = targetEntity;
            AssertNoAllocation(probe);
            Assert.That(absent, Is.EqualTo(6));
            Assert.That(target.EntityCount, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => target.Get<AbsentA>(targetEntity));
            target.Set(targetEntity, new AbsentA());
            Assert.That(target.Has<AbsentA>(targetEntity), Is.True);
            Assert.That(target.Has<AbsentB>(targetEntity), Is.False);
            Assert.That(target.Remove<AbsentA>(targetEntity), Is.True);
        }

        [Test]
        public void AbsentTryGet_InitializesOutputAndDoesNotAlterExistingComponents()
        {
            using var world = new World();
            var e = world.CreateEntity();
            world.Set(e, new Beta { Value = 45 });
            Alpha value = new Alpha { Value = 99, Reference = new object() };
            Assert.That(world.TryGet(e, out value), Is.False);
            Assert.That(value.Value, Is.Zero);
            Assert.That(value.Reference, Is.Null);
            Assert.That(world.Remove<Alpha>(e), Is.False);
            Assert.That(world.Get<Beta>(e).Value, Is.EqualTo(45));
        }

        [Test]
        public void DisposedWorld_RejectsLookupsCreationAndReservation()
        {
            var world = new World();
            var e = world.CreateEntity();
            world.Set(e, new Alpha());
            var cached = world.Query().WithAll<Alpha>();
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.Get<Alpha>(e));
            Assert.Throws<ObjectDisposedException>(() => world.Has<Alpha>(e));
            Assert.Throws<ObjectDisposedException>(() => world.TryGet(e, out Alpha _));
            Assert.Throws<ObjectDisposedException>(() => world.Remove<Alpha>(e));
            Assert.Throws<ObjectDisposedException>(() => world.Set(e, new Beta()));
            Assert.Throws<ObjectDisposedException>(() => world.EnsureCapacity<Beta>(16));
            Assert.Throws<ObjectDisposedException>(() => Assert.That(cached.Count, Is.Zero));
            Assert.DoesNotThrow(() => world.Dispose());
        }

        [Test]
        public void CapacityAndCachedQuerySurviveRegistrationOfOtherTypesAndDespawn()
        {
            using var world = new World(1);
            world.EnsureEntityCapacity(128);
            world.EnsureCapacity<Alpha>(128);
            var query = world.Query().WithAll<Alpha>();
            var first = world.CreateEntity(); var second = world.CreateEntity();
            world.Set(first, new Alpha { Value = 7 });
            world.Set(second, new Alpha { Value = 13 });
            RegisterAllMarkers(world, first); // Grow this world's type table without replacing Alpha's pool.
            world.EnsureCapacity<Beta>(128);
            world.Set(first, new Beta { Value = 31 });
            world.RequestDespawn(second);
            var visitor = new SumVisitor();
            Assert.That(query.ForEach<SumVisitor, Alpha>(ref visitor), Is.EqualTo(1));
            Assert.That(visitor.Sum, Is.EqualTo(7));
            visitor = default;
            Assert.That(query.OnlyDespawning().ForEach<SumVisitor, Alpha>(ref visitor), Is.EqualTo(1));
            Assert.That(visitor.Sum, Is.EqualTo(13));
            Assert.That(query.IncludeDespawning().Count, Is.EqualTo(2));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(query.Count, Is.EqualTo(1));
            Assert.That(world.Get<Beta>(first).Value, Is.EqualTo(31));
            world.DestroyEntity(first);
            Assert.That(query.Count, Is.Zero);
            var recycled = world.CreateEntity();
            Assert.That(world.Has<Alpha>(recycled), Is.False);
            Assert.That(world.Has<ThreadValue<M7>>(recycled), Is.False);
        }

        private struct SumVisitor : IEntityVisitor<Alpha>
        {
            public int Sum;
            public void Visit(Entity e, ref Alpha value) => Sum += value.Value;
        }

        private static void RegisterAllMarkers(World world, Entity entity)
        {
            world.Set(entity, new ThreadValue<M0> { Value = 0 });
            world.Set(entity, new ThreadValue<M1> { Value = 1 });
            world.Set(entity, new ThreadValue<M2> { Value = 2 });
            world.Set(entity, new ThreadValue<M3> { Value = 3 });
            world.Set(entity, new ThreadValue<M4> { Value = 4 });
            world.Set(entity, new ThreadValue<M5> { Value = 5 });
            world.Set(entity, new ThreadValue<M6> { Value = 6 });
            world.Set(entity, new ThreadValue<M7> { Value = 7 });
        }

        // Unique types used only by the concurrent fixture, so other tests cannot prewarm their IDs.
        private struct R0 { }
        private struct R1 { }
        private struct R2 { }
        private struct R3 { }
        private struct R4 { }
        private struct R5 { }
        private struct R6 { }
        private struct R7 { }

        [Test]
        public void IndependentWorlds_CanRegisterDistinctAndSharedTypesConcurrently()
        {
            using var start = new ManualResetEventSlim(false);
            var errors = new Exception[8];
            var threads = new[]
            {
                new Thread(() => RegisterOnWorker<R0>(0, start, errors)),
                new Thread(() => RegisterOnWorker<R1>(1, start, errors)),
                new Thread(() => RegisterOnWorker<R2>(2, start, errors)),
                new Thread(() => RegisterOnWorker<R3>(3, start, errors)),
                new Thread(() => RegisterOnWorker<R4>(4, start, errors)),
                new Thread(() => RegisterOnWorker<R5>(5, start, errors)),
                new Thread(() => RegisterOnWorker<R6>(6, start, errors)),
                new Thread(() => RegisterOnWorker<R7>(7, start, errors))
            };
            foreach (var thread in threads) thread.Start();
            start.Set();
            foreach (var thread in threads) thread.Join();
            foreach (var error in errors) Assert.That(error, Is.Null);
            // Put ALL raced types in one world afterward: cross-world ID collisions must not hide.
            using var combined = new World();
            var e = combined.CreateEntity();
            CheckRacedType<R0>(combined, e, 0); CheckRacedType<R1>(combined, e, 1);
            CheckRacedType<R2>(combined, e, 2); CheckRacedType<R3>(combined, e, 3);
            CheckRacedType<R4>(combined, e, 4); CheckRacedType<R5>(combined, e, 5);
            CheckRacedType<R6>(combined, e, 6); CheckRacedType<R7>(combined, e, 7);
            Assert.That(combined.Get<ThreadValue<R0>>(e).Value, Is.Zero);
            Assert.That(combined.Get<ThreadValue<R7>>(e).Value, Is.EqualTo(7));
        }

        private static void RegisterOnWorker<T>(int index, ManualResetEventSlim start, Exception[] errors) where T : struct
        {
            try
            {
                using var world = new World();
                var entity = world.CreateEntity();
                start.Wait();
                world.EnsureCapacity<ThreadValue<T>>(8);
                world.Set(entity, new ThreadValue<T> { Value = index });
                world.Set(entity, new SharedRace { Value = index + 100 });
                if (world.Get<ThreadValue<T>>(entity).Value != index || world.Get<SharedRace>(entity).Value != index + 100)
                    throw new InvalidOperationException("Concurrent registration mixed component contents.");
            }
            catch (Exception error) { errors[index] = error; }
        }

        private static void CheckRacedType<T>(World world, Entity entity, int expected) where T : struct
        {
            world.Set(entity, new ThreadValue<T> { Value = expected });
            Assert.That(world.Get<ThreadValue<T>>(entity).Value, Is.EqualTo(expected));
        }

        [Test]
        public void ReservedInsertionAndWarmLookups_HaveNoManagedAllocations()
        {
            const int count = 1024;
            using var warm = new World(1);
            using var target = new World(1);
            var handles = new Entity[count];
            World active = warm;
            TestDelegate populate = () =>
            {
                for (int i = 0; i < count; i++)
                {
                    handles[i] = active.CreateEntity();
                    active.Set(handles[i], new Alpha { Value = i });
                    active.Set(handles[i], new Beta { Value = i + 1 });
                }
            };
            populate();
            target.EnsureEntityCapacity(count);
            target.EnsureCapacity<Beta>(count); // Opposite registration order from warm.
            target.EnsureCapacity<Alpha>(count);
            active = target;
            AssertNoAllocation(populate);
            int sum = 0, misses = 0;
            TestDelegate read = () =>
            {
                sum = 0; misses = 0;
                for (int i = 0; i < count; i++)
                {
                    if (!target.Has<Alpha>(handles[i])) misses++;
                    if (target.TryGet(handles[i], out Beta value)) sum += (int)value.Value;
                    else misses++;
                    sum += target.Get<Alpha>(handles[i]).Value;
                }
            };
            read();
            AssertNoAllocation(read);
            Assert.That(misses, Is.Zero);
            Assert.That(sum, Is.EqualTo(count * count));
            Assert.That(target.EntityCount, Is.EqualTo(count));
        }

        private static void AssertNoAllocation(TestDelegate body)
        {
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
#endif
        }

        [Test]
        public void AllocationDetector_ObservesKnownManagedAllocation()
        {
            byte[] retained = null;
            TestDelegate body = () => retained = new byte[1024];
            body();
#if UNITY_5_3_OR_NEWER
            Assert.That(body, new AllocatingGCMemoryConstraint());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.GreaterThanOrEqualTo(1024));
#endif
            GC.KeepAlive(retained);
        }
    }
}
