using System;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.Tests.Ecs
{
    public class CapacityReservationTests
    {
        private struct Value { public int Number; public object Reference; }
        private struct Rare { public int Number; }

        [Test]
        public void Reservation_PreservesEntitiesValuesPendingDespawnAndRecycledHandles()
        {
            using var world = new World(1);
            var first = world.CreateEntity();
            var hole = world.CreateEntity();
            var pending = world.CreateEntity();
            var marker = new object();
            world.Set(first, new Value { Number = 17, Reference = marker });
            world.Set(pending, new Value { Number = 23 });
            world.DestroyEntity(hole);
            world.RequestDespawn(pending);
            var cached = world.Query().WithAll<Value>();

            world.EnsureEntityCapacity(128);
            world.EnsureCapacity<Value>(128);
            world.EnsureEntityCapacity(1);
            world.EnsureCapacity<Value>(1);
            world.EnsureEntityCapacity(0);
            world.EnsureCapacity<Value>(0);

            Assert.That(world.EntityCount, Is.EqualTo(2));
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.IsAlive(first), Is.True);
            Assert.That(world.IsAlive(pending), Is.True);
            Assert.That(world.IsDespawning(pending), Is.True);
            Assert.That(world.IsAlive(hole), Is.False);
            Assert.That(world.Get<Value>(first).Number, Is.EqualTo(17));
            Assert.That(world.Get<Value>(first).Reference, Is.SameAs(marker));
            Assert.That(world.Get<Value>(pending).Number, Is.EqualTo(23));
            Assert.That(cached.Count, Is.EqualTo(1));
            Assert.That(cached.IncludeDespawning().Count, Is.EqualTo(2));
            var reused = world.CreateEntity();
            Assert.That(reused.Index, Is.EqualTo(hole.Index));
            Assert.That(reused.Version, Is.Not.EqualTo(hole.Version));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(world.IsAlive(reused), Is.True);
        }

        [Test]
        public void Reservation_PreservesUnactivatedCommandBufferHandlesAndOwners()
        {
            using var world = new World(1);
            using var commands = new CommandBuffer(world);
            var reserved = commands.CreateEntity();
            commands.Set(reserved, new Value { Number = 42 });
            world.EnsureEntityCapacity(64);
            world.EnsureCapacity<Value>(64);
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.IsAlive(reserved), Is.False);
            Assert.That(commands.Count, Is.EqualTo(2));
            commands.Playback();
            Assert.That(world.IsAlive(reserved), Is.True);
            Assert.That(world.Get<Value>(reserved).Number, Is.EqualTo(42));
            Assert.That(world.EntityCount, Is.EqualTo(1));
        }

        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(0x7FF00000)]
        [TestCase(int.MaxValue)]
        public void InvalidCapacity_IsRejectedBeforeChangingLogicalState(int capacity)
        {
            using var world = new World(1);
            var entity = world.CreateEntity();
            world.Set(entity, new Value { Number = 31 });
            world.RequestDespawn(entity);
            var entityError = Assert.Throws<ArgumentOutOfRangeException>(() => world.EnsureEntityCapacity(capacity));
            var componentError = Assert.Throws<ArgumentOutOfRangeException>(() => world.EnsureCapacity<Value>(capacity));
            Assert.That(entityError.ParamName, Is.EqualTo("capacity"));
            Assert.That(componentError.ParamName, Is.EqualTo("capacity"));
            Assert.That(world.EntityCount, Is.EqualTo(1));
            Assert.That(world.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(world.Get<Value>(entity).Number, Is.EqualTo(31));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1024)]
        public void Iteration_RejectsReservationEvenWhenNoGrowthIsNeeded(int capacity)
        {
            using var world = new World(1);
            var entity = world.CreateEntity();
            world.Set(entity, new Value());
            world.EnsureEntityCapacity(16);
            world.EnsureCapacity<Value>(16);
            world.Query().ForEach<Value>((Entity e, ref Value value) =>
            {
                Assert.Throws<InvalidOperationException>(() => world.EnsureEntityCapacity(capacity));
                Assert.Throws<InvalidOperationException>(() => world.EnsureCapacity<Value>(capacity));
                Assert.Throws<InvalidOperationException>(() => world.EnsureCapacity<Rare>(capacity));
                value.Number = 7;
            });
            Assert.That(world.Get<Value>(entity).Number, Is.EqualTo(7));
            Assert.DoesNotThrow(() => world.EnsureEntityCapacity(capacity));
            Assert.DoesNotThrow(() => world.EnsureCapacity<Value>(capacity));
        }

        [Test]
        public void DisposedWorld_RejectsEvenZeroReservation()
        {
            var world = new World();
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.EnsureEntityCapacity(0));
            Assert.Throws<ObjectDisposedException>(() => world.EnsureCapacity<Value>(0));
        }

        [Test]
        public void EmptyWorld_ReservationDoesNotCreateEntitiesOrComponents()
        {
            using var world = new World(1);
            world.EnsureEntityCapacity(0);
            world.EnsureCapacity<Value>(0);
            world.EnsureEntityCapacity(32);
            world.EnsureCapacity<Value>(32);
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.PendingDespawnCount, Is.Zero);
            Assert.That(world.Query().WithAll<Value>().Count, Is.Zero);
            var first = world.CreateEntity();
            Assert.That(first.Index, Is.Zero);
            Assert.That(first.Version, Is.EqualTo(1));
            Assert.That(world.Has<Value>(first), Is.False);
        }

        [Test]
        public void ReservedEntityAndComponentStorage_AllowFirstPopulationWithoutAllocation()
        {
            const int count = 10000;
            using var warm = new World(1);
            using var target = new World(1);
            var handles = new Entity[count];
            World active = warm;
            TestDelegate populate = () =>
            {
                for (int i = 0; i < count; i++)
                {
                    handles[i] = active.CreateEntity();
                    active.Set(handles[i], new Value { Number = i });
                }
            };
            populate(); // Warm the exact delegate/generic path on a DIFFERENT world.
            target.EnsureEntityCapacity(count);
            target.EnsureCapacity<Value>(count);
            target.EnsureEntityCapacity(1); // Smaller requests must not shrink reserved storage.
            target.EnsureCapacity<Value>(1);
            active = target;
            AssertNoAllocation(populate); // First population of target; warming it would mask missing reservation.
            Assert.That(target.EntityCount, Is.EqualTo(count));
            Assert.That(handles[count - 1].Index, Is.EqualTo(count - 1));
            for (int i = 0; i < count; i++) Assert.That(target.Get<Value>(handles[i]).Number, Is.EqualTo(i));
        }

        [Test]
        public void RareComponent_ReservationCoversHighEntityIndexNotJustComponentCount()
        {
            const int capacity = 1024;
            using var warm = new World(capacity);
            using var target = new World(capacity);
            Entity warmLast = default, targetLast = default;
            for (int i = 0; i < capacity; i++)
            {
                warmLast = warm.CreateEntity();
                targetLast = target.CreateEntity();
            }
            World active = warm;
            Entity last = warmLast;
            TestDelegate insert = () => active.Set(last, new Rare { Number = 99 });
            insert();
            Assert.That(targetLast.Index, Is.EqualTo(capacity - 1));
            target.EnsureCapacity<Rare>(capacity); // Exclusive index bound, despite currently having zero Rare values.
            active = target; last = targetLast;
            AssertNoAllocation(insert);
            Assert.That(target.Get<Rare>(targetLast).Number, Is.EqualTo(99));
            Assert.That(target.Query().WithAll<Rare>().Count, Is.EqualTo(1));
            Assert.That(target.EntityCount, Is.EqualTo(capacity));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(8)]
        public void AutomaticGrowthAfterReservation_PreservesSwappedSparseMappings(int reservedCapacity)
        {
            const int count = 35;
            using var world = new World(count);
            world.EnsureCapacity<Value>(reservedCapacity);
            var query = world.Query().WithAll<Value>();
            var handles = new Entity[count];
            for (int i = 0; i < count; i++) handles[i] = world.CreateEntity();
            var marker = new object();
            // Insert high indices first so sparse growth and dense growth are independent.
            for (int i = count - 1; i >= 26; i--)
                world.Set(handles[i], new Value { Number = i * 7, Reference = marker });
            Assert.That(world.Remove<Value>(handles[31]), Is.True); // Swap an existing dense entry before further growth.
            for (int i = 25; i >= 0; i--)
                world.Set(handles[i], new Value { Number = i * 7, Reference = marker });
            world.Set(handles[31], new Value { Number = 777, Reference = marker });
            Assert.That(query.Count, Is.EqualTo(count));
            Assert.That(world.Remove<Value>(handles[5]), Is.True); // Validate inverse mapping after growth too.
            Assert.That(world.Has<Value>(handles[5]), Is.False);
            int visited = 0;
            query.ForEach<Value>((Entity e, ref Value value) =>
            {
                Assert.That(e, Is.EqualTo(handles[e.Index]));
                Assert.That(e.Index, Is.Not.EqualTo(5));
                Assert.That(value.Number, Is.EqualTo(e.Index == 31 ? 777 : e.Index * 7));
                Assert.That(value.Reference, Is.SameAs(marker));
                visited++;
            });
            Assert.That(visited, Is.EqualTo(count - 1));
            for (int i = 0; i < count; i++)
            {
                Assert.That(world.IsAlive(handles[i]), Is.True);
                if (i != 5) Assert.That(world.Get<Value>(handles[i]).Number, Is.EqualTo(i == 31 ? 777 : i * 7));
            }
            Assert.That(world.EntityCount, Is.EqualTo(count));
        }

        [Test]
        public void ReservationAfterAutomaticGrowth_PreservesValuesThroughLaterGrowth()
        {
            using var world = new World(32);
            var handles = new Entity[32];
            for (int i = 0; i < handles.Length; i++) handles[i] = world.CreateEntity();
            for (int i = 0; i < 5; i++) world.Set(handles[i], new Value { Number = i + 100 });
            world.EnsureCapacity<Value>(9); // Exact non-power-of-two capacity, beyond the initial automatic growth.
            for (int i = 5; i < handles.Length; i++) world.Set(handles[i], new Value { Number = i + 100 });
            Assert.That(world.Query().WithAll<Value>().Count, Is.EqualTo(handles.Length));
            for (int i = 0; i < handles.Length; i++)
                Assert.That(world.Get<Value>(handles[i]).Number, Is.EqualTo(i + 100));
            Assert.That(world.Remove<Value>(handles[0]), Is.True);
            Assert.That(world.Get<Value>(handles[31]).Number, Is.EqualTo(131));
            Assert.That(world.Remove<Value>(handles[31]), Is.True);
            Assert.That(world.Query().WithAll<Value>().Count, Is.EqualTo(30));
        }

        [Test]
        public void RepeatedAndSmallerReservations_DoNotAllocate()
        {
            using var world = new World(1);
            world.EnsureEntityCapacity(64);
            world.EnsureCapacity<Value>(64);
            TestDelegate reserve = () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    world.EnsureEntityCapacity(64); world.EnsureCapacity<Value>(64);
                    world.EnsureEntityCapacity(1); world.EnsureCapacity<Value>(1);
                    world.EnsureEntityCapacity(0); world.EnsureCapacity<Value>(0);
                }
            };
            reserve();
            AssertNoAllocation(reserve);
            Assert.That(world.EntityCount, Is.Zero);
        }

        private static void AssertNoAllocation(TestDelegate body)
        {
            // Caller warms the exact delegate on separate storage when testing first insertion.
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            Assert.That(MeasureManagedAllocation(body), Is.Zero);
#endif
        }

#if !UNITY_5_3_OR_NEWER
        private static long MeasureManagedAllocation(TestDelegate body)
        {
            // CoreCLR background marking voids unused thread allocation contexts. That can
            // raise GetAllocatedBytesForCurrentThread even across a loop with no allocations.
            // Isolate the measurement from GC; retain default JIT/tiering and count all actual
            // allocations. Enter/exit are outside the interval, and the positive control below
            // uses this same path so accidental allocations still fail the zero-byte assertion.
            Assert.That(GC.TryStartNoGCRegion(16 * 1024 * 1024), Is.True,
                "Allocation measurement requires a short isolated GC interval.");
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            finally { GC.EndNoGCRegion(); }
        }
#endif

        [Test]
        public void AllocationDetector_ObservesKnownManagedAllocation()
        {
            byte[] retained = null;
            TestDelegate body = () => retained = new byte[1024];
            body();
#if UNITY_5_3_OR_NEWER
            Assert.That(body, new AllocatingGCMemoryConstraint());
#else
            Assert.That(MeasureManagedAllocation(body), Is.GreaterThanOrEqualTo(1024));
#endif
            GC.KeepAlive(retained);
        }
    }
}
