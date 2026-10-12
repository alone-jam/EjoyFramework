using System;
using System.Collections.Generic;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.Tests.Ecs
{
    public class PartitionTests
    {
        private struct Collector : IEntityVisitor
        {
            public List<Entity> Items;
            public int Count;
            public int ThrowAt;
            public void Visit(Entity e)
            {
                Count++; Items?.Add(e);
                if (ThrowAt != 0 && Count == ThrowAt) throw new ApplicationException();
            }
        }

        private static void AssertPartition(World world, int key, params Entity[] expected)
        {
            var visitor = new Collector { Items = new List<Entity>() };
            Assert.That(world.GetPartitionCount(key), Is.EqualTo(expected.Length));
            Assert.That(world.ForEachInPartition(key, ref visitor), Is.EqualTo(expected.Length));
            Assert.That(visitor.Count, Is.EqualTo(expected.Length));
            Assert.That(visitor.Items, Is.EqualTo(expected));
        }

        [Test]
        public void InterleavedReservations_SkipManualKeysAndNeverAutomaticallyReuseReleasedKeys()
        {
            using var world = new World();
            var manual = world.CreateEntity(); var maximum = world.CreateEntity();
            world.SetPartition(manual, 1); world.SetPartition(maximum, int.MaxValue);
            int firstOwner = world.ReservePartitionKey();
            int secondOwner = world.ReservePartitionKey();
            Assert.That(firstOwner, Is.Zero);
            Assert.That(secondOwner, Is.EqualTo(2));
            AssertPartition(world, firstOwner); AssertPartition(world, secondOwner);
            Assert.That(world.ReleasePartitionKey(firstOwner), Is.True);
            Assert.That(world.ReleasePartitionKey(firstOwner), Is.False);
            Assert.That(world.ReservePartitionKey(), Is.EqualTo(3));
            Assert.That(world.ReleasePartitionKey(secondOwner), Is.True);
            world.ClearPartition(manual); // A manual hole behind the cursor is not reused either.
            Assert.That(world.ReservePartitionKey(), Is.EqualTo(4));
            AssertPartition(world, int.MaxValue, maximum);
            Assert.That(world.ReleasePartitionKey(int.MaxValue), Is.False);
            Assert.That(world.ReleasePartitionKey(123), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => world.ReleasePartitionKey(-1));
        }

        [Test]
        public void EmptyReservations_SurviveClearMoveAndImmediateDestructionUntilReleased()
        {
            using var world = new World();
            int cleared = world.ReservePartitionKey();
            int moved = world.ReservePartitionKey();
            int destroyed = world.ReservePartitionKey();
            var e = world.CreateEntity();
            world.SetPartition(e, cleared); world.ClearPartition(e);
            world.SetPartition(e, moved); world.SetPartition(e, destroyed);
            world.DestroyEntity(e);
            AssertPartition(world, cleared); AssertPartition(world, moved); AssertPartition(world, destroyed);
            Assert.That(world.ReleasePartitionKey(cleared), Is.True);
            Assert.That(world.ReleasePartitionKey(moved), Is.True);
            Assert.That(world.ReleasePartitionKey(destroyed), Is.True);
            Assert.That(world.ReservePartitionKey(), Is.EqualTo(3));
        }

        [Test]
        public void NonemptyReservation_CannotBeReleasedWhileMembersAreAliveOrPending()
        {
            using var world = new World();
            int key = world.ReservePartitionKey();
            var e = world.CreateEntity(); world.SetPartition(e, key);
            Assert.Throws<InvalidOperationException>(() => world.ReleasePartitionKey(key));
            Assert.That(world.RequestDespawnPartition(key), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => world.ReleasePartitionKey(key));
            AssertPartition(world, key, e);
            world.ProcessDespawns();
            AssertPartition(world, key);
            Assert.That(world.ReleasePartitionKey(key), Is.True);
            Assert.That(world.ReservePartitionKey(), Is.GreaterThan(key));
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.ReservePartitionKey());
            Assert.Throws<ObjectDisposedException>(() => world.ReleasePartitionKey(key));
        }

        [Test]
        public void MovesAndUnlinks_PreserveInsertionOrderAndExactCounts()
        {
            using var world = new World();
            var a = world.CreateEntity(); var b = world.CreateEntity();
            var c = world.CreateEntity(); var d = world.CreateEntity();
            foreach (var e in new[] { a, b, c, d }) world.SetPartition(e, 1);
            world.SetPartition(b, 1); // Same-key assignment must not move b to the tail.
            AssertPartition(world, 1, a, b, c, d);
            Assert.That(world.ClearPartition(b), Is.True);
            Assert.That(world.ClearPartition(b), Is.False);
            AssertPartition(world, 1, a, c, d);
            world.SetPartition(a, 2); // Head unlink and move.
            AssertPartition(world, 1, c, d); AssertPartition(world, 2, a);
            world.ClearPartition(d); // Tail unlink.
            AssertPartition(world, 1, c);
            world.ClearPartition(c); // Last member removes the partition record.
            AssertPartition(world, 1);
            world.SetPartition(a, 1);
            world.SetPartition(b, 1); world.SetPartition(d, 1);
            world.ClearPartition(a);
            AssertPartition(world, 1, b, d); AssertPartition(world, 2);
            world.EnsureEntityCapacity(1024);
            AssertPartition(world, 1, b, d);
        }

        [Test]
        public void PendingMembersRemainIncluded_UntilImmediateOrQueuedDestructionDetachesThem()
        {
            using var world = new World(1);
            var a = world.CreateEntity(); var b = world.CreateEntity();
            world.SetPartition(a, 7); world.SetPartition(b, 7);
            Assert.That(world.RequestDespawnPartition(7), Is.EqualTo(2));
            Assert.That(world.RequestDespawnPartition(7), Is.Zero);
            AssertPartition(world, 7, a, b);
            Assert.That(world.Query().Count, Is.Zero);
            world.DestroyEntity(a);
            var reused = world.CreateEntity();
            Assert.That(reused.Index, Is.EqualTo(a.Index));
            Assert.That(reused.Version, Is.Not.EqualTo(a.Version));
            AssertPartition(world, 7, b);
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            AssertPartition(world, 7);
            Assert.That(world.IsAlive(reused), Is.True);
            using var commands = new CommandBuffer(world);
            world.SetPartition(reused, 7);
            commands.DestroyEntity(reused); commands.Playback();
            AssertPartition(world, 7, reused);
            world.ProcessDespawns();
            AssertPartition(world, 7);
        }

        [Test]
        public void BoundedDespawn_CountsOnlyNewRequestsAndKeepsMembershipUntilDestruction()
        {
            using var world = new World();
            var a = world.CreateEntity(); var b = world.CreateEntity();
            var c = world.CreateEntity(); var d = world.CreateEntity();
            foreach (var e in new[] { a, b, c, d }) world.SetPartition(e, int.MaxValue);
            world.RequestDespawn(a);
            Assert.That(world.RequestDespawnPartition(int.MaxValue, 0), Is.Zero);
            Assert.That(world.IsDespawning(b), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => world.RequestDespawnPartition(int.MaxValue, -1));
            Assert.That(world.RequestDespawnPartition(int.MaxValue, 2), Is.EqualTo(2));
            Assert.That(world.IsDespawning(b), Is.True);
            Assert.That(world.IsDespawning(c), Is.True);
            Assert.That(world.IsDespawning(d), Is.False);
            AssertPartition(world, int.MaxValue, a, b, c, d);
            Assert.That(world.RequestDespawnPartition(int.MaxValue, 2), Is.EqualTo(1));
            Assert.That(world.RequestDespawnPartition(int.MaxValue, 2), Is.Zero);
            Assert.That(world.RequestDespawnPartition(123, 2), Is.Zero);
            Assert.That(world.ProcessDespawns(), Is.EqualTo(4));
            AssertPartition(world, int.MaxValue);
        }

        [Test]
        public void SparseMaximumKeysAndIndependentWorlds_DoNotShareMembership()
        {
            using var first = new World(); using var second = new World();
            var a = first.CreateEntity(); var b = first.CreateEntity(); var other = second.CreateEntity();
            first.SetPartition(a, 0); first.SetPartition(b, int.MaxValue);
            second.SetPartition(other, int.MaxValue);
            AssertPartition(first, 0, a); AssertPartition(first, int.MaxValue, b);
            AssertPartition(second, int.MaxValue, other);
            Assert.That(first.RequestDespawnPartition(int.MaxValue), Is.EqualTo(1));
            first.ProcessDespawns();
            AssertPartition(first, int.MaxValue); AssertPartition(second, int.MaxValue, other);
            Assert.That(second.IsDespawning(other), Is.False);
        }

        [Test]
        public void InvalidKeysHandlesAndDisposedWorld_AreRejected()
        {
            using var world = new World(); using var other = new World();
            using var commands = new CommandBuffer(world);
            var live = world.CreateEntity(); var stale = world.CreateEntity(); world.DestroyEntity(stale);
            foreach (var invalid in new[] { default(Entity), stale, other.CreateEntity(), commands.CreateEntity() })
            {
                Assert.Throws<InvalidOperationException>(() => world.SetPartition(invalid, 0));
                Assert.Throws<InvalidOperationException>(() => world.ClearPartition(invalid));
            }
            var visitor = new Collector { Count = 5 };
            Assert.Throws<ArgumentOutOfRangeException>(() => world.SetPartition(live, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPartitionCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.ForEachInPartition(-1, ref visitor));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.RequestDespawnPartition(-1));
            Assert.That(world.ForEachInPartition(123, ref visitor), Is.Zero);
            Assert.That(visitor.Count, Is.EqualTo(5));
            Assert.That(world.RequestDespawnPartition(123), Is.Zero);
            world.SetPartition(live, 0); world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.GetPartitionCount(0));
            Assert.Throws<ObjectDisposedException>(() => world.ForEachInPartition(0, ref visitor));
            Assert.Throws<ObjectDisposedException>(() => world.RequestDespawnPartition(0));
            Assert.Throws<ObjectDisposedException>(() => world.SetPartition(live, 0));
            Assert.Throws<ObjectDisposedException>(() => world.ClearPartition(live));
        }

        [Test]
        public void CallbackException_WritesBackPartialStateAndReleasesStructuralGuard()
        {
            using var world = new World();
            var a = world.CreateEntity(); var b = world.CreateEntity(); var c = world.CreateEntity();
            world.SetPartition(a, 1); world.SetPartition(b, 1); world.SetPartition(c, 1);
            var visitor = new Collector { Items = new List<Entity>(), ThrowAt = 2 };
            Assert.Throws<ApplicationException>(() => world.ForEachInPartition(1, ref visitor));
            Assert.That(visitor.Count, Is.EqualTo(2));
            Assert.That(visitor.Items, Is.EqualTo(new[] { a, b }));
            Assert.DoesNotThrow(() => world.ClearPartition(c));
            AssertPartition(world, 1, a, b);
        }

        private struct Mutator : IEntityVisitor
        {
            public World World;
            public int Operation;
            public int Count;
            public void Visit(Entity e)
            {
                Count++;
                switch (Operation)
                {
                    case 0: World.SetPartition(e, 1); break;
                    case 1: World.ClearPartition(e); break;
                    case 2: World.DestroyEntity(e); break;
                    case 3: World.EnsureEntityCapacity(1024); break;
                    case 4: World.ProcessDespawns(); break;
                    case 5: World.ReservePartitionKey(); break;
                    case 6: World.ReleasePartitionKey(1); break;
                }
            }
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5), TestCase(6)]
        public void MembershipAndStructuralMutation_AreRejectedDuringTraversal(int operation)
        {
            using var world = new World();
            var e = world.CreateEntity(); world.SetPartition(e, 1);
            var visitor = new Mutator { World = world, Operation = operation };
            Assert.Throws<InvalidOperationException>(() => world.ForEachInPartition(1, ref visitor));
            Assert.That(visitor.Count, Is.EqualTo(1));
            AssertPartition(world, 1, e);
            Assert.DoesNotThrow(() => world.ClearPartition(e));
        }

        private struct MarkPartition : IEntityVisitor
        {
            public World World;
            public int Count;
            public int Requested;
            public void Visit(Entity e) { Count++; Requested += World.RequestDespawnPartition(1); }
        }

        [Test]
        public void PartitionDespawn_CanBeRequestedDuringTraversalWithoutSkippingMembers()
        {
            using var world = new World();
            var a = world.CreateEntity(); var b = world.CreateEntity();
            world.SetPartition(a, 1); world.SetPartition(b, 1);
            var visitor = new MarkPartition { World = world };
            Assert.That(world.ForEachInPartition(1, ref visitor), Is.EqualTo(2));
            Assert.That(visitor.Count, Is.EqualTo(2)); Assert.That(visitor.Requested, Is.EqualTo(2));
            AssertPartition(world, 1, a, b);
            world.Query().IncludeDespawning().ForEach(e =>
            {
                Assert.Throws<InvalidOperationException>(() => world.SetPartition(e, 2));
                Assert.Throws<InvalidOperationException>(() => world.ClearPartition(e));
            });
            Assert.That(world.ProcessDespawns(), Is.EqualTo(2));
            AssertPartition(world, 1);
        }

        [Test]
        public void RandomizedOperations_MatchOrderedReferenceModel()
        {
            using var world = new World(32);
            var entities = new Entity[32];
            for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity();
            var keys = new[] { 0, 1, 2, int.MaxValue };
            var membership = new Dictionary<Entity, int>();
            var expected = new Dictionary<int, List<Entity>>();
            foreach (int key in keys) expected.Add(key, new List<Entity>());
            var random = new Random(52173);
            for (int step = 0; step < 10000; step++)
            {
                int index = random.Next(entities.Length);
                var entity = entities[index];
                switch (random.Next(5))
                {
                    case 0:
                        int destinationKey = keys[random.Next(keys.Length)];
                        world.SetPartition(entity, destinationKey);
                        if (!membership.TryGetValue(entity, out int prior) || prior != destinationKey)
                        {
                            RemoveExpected(entity, membership, expected);
                            membership.Add(entity, destinationKey); expected[destinationKey].Add(entity);
                        }
                        break;
                    case 1:
                        Assert.That(world.ClearPartition(entity), Is.EqualTo(membership.ContainsKey(entity)));
                        RemoveExpected(entity, membership, expected); break;
                    case 2:
                        world.DestroyEntity(entity); RemoveExpected(entity, membership, expected);
                        entities[index] = world.CreateEntity(); break;
                    case 3: world.RequestDespawn(entity); break;
                    case 4:
                        world.ProcessDespawns();
                        for (int i = 0; i < entities.Length; i++)
                            if (!world.IsAlive(entities[i]))
                            {
                                RemoveExpected(entities[i], membership, expected);
                                entities[i] = world.CreateEntity();
                            }
                        break;
                }
                foreach (int key in keys) AssertPartition(world, key, expected[key].ToArray());
            }
        }

        private static void RemoveExpected(Entity entity, Dictionary<Entity, int> membership,
            Dictionary<int, List<Entity>> expected)
        {
            if (!membership.TryGetValue(entity, out int key)) return;
            expected[key].Remove(entity); membership.Remove(entity);
        }

        [Test]
        public void WarmPartitionLifecycle_HasNoManagedAllocations()
        {
            using var world = new World(1);
            int visited = 0, requested = 0;
            TestDelegate body = () =>
            {
                var visitor = new Collector();
                requested = 0;
                for (int i = 0; i < 10000; i++)
                {
                    var e = world.CreateEntity();
                    world.SetPartition(e, 0); world.SetPartition(e, int.MaxValue);
                    world.ClearPartition(e); world.SetPartition(e, int.MaxValue);
                    world.ForEachInPartition(int.MaxValue, ref visitor);
                    requested += world.RequestDespawnPartition(int.MaxValue);
                    world.ProcessDespawns();
                }
                visited = visitor.Count;
            };
            body();
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
#endif
            Assert.That(visited, Is.EqualTo(10000)); Assert.That(requested, Is.EqualTo(10000));
            Assert.That(world.GetPartitionCount(0), Is.Zero);
            Assert.That(world.GetPartitionCount(int.MaxValue), Is.Zero);
            Assert.That(world.EntityCount, Is.Zero);
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
