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
    public class QueryStructVisitorTests
    {
        private struct A { public int Value; }
        private struct B { public int Value; }
        private struct C { public int Value; }
        private struct D { public int Value; }
        private struct Blocked { }

        private struct Accumulator : IEntityVisitor, IEntityVisitor<A>, IEntityVisitor<A, B>,
            IEntityVisitor<A, B, C>, IEntityVisitor<A, B, C, D>
        {
            public int Count;
            public int ThrowAt;
            public Entity Last;
            public void Visit(Entity e) => Record(e);
            public void Visit(Entity e, ref A a) { a.Value++; Record(e); }
            public void Visit(Entity e, ref A a, ref B b) { a.Value++; b.Value++; Record(e); }
            public void Visit(Entity e, ref A a, ref B b, ref C c) { a.Value++; b.Value++; c.Value++; Record(e); }
            public void Visit(Entity e, ref A a, ref B b, ref C c, ref D d)
                { a.Value++; b.Value++; c.Value++; d.Value++; Record(e); }
            private void Record(Entity e)
            {
                Count++; Last = e;
                if (ThrowAt != 0 && Count == ThrowAt) throw new ApplicationException("visitor failure");
            }
        }

        private static Entity Spawn(World world, int missing = 0)
        {
            var e = world.CreateEntity();
            if (missing != 1) world.Set(e, new A { Value = 1 });
            if (missing != 2) world.Set(e, new B { Value = 2 });
            if (missing != 3) world.Set(e, new C { Value = 3 });
            if (missing != 4) world.Set(e, new D { Value = 4 });
            return e;
        }

        private static int Run(Query query, int arity, ref Accumulator visitor)
        {
            switch (arity)
            {
                case 0: return query.ForEach<Accumulator>(ref visitor);
                case 1: return query.ForEach<Accumulator, A>(ref visitor);
                case 2: return query.ForEach<Accumulator, A, B>(ref visitor);
                case 3: return query.ForEach<Accumulator, A, B, C>(ref visitor);
                case 4: return query.ForEach<Accumulator, A, B, C, D>(ref visitor);
                default: throw new ArgumentOutOfRangeException(nameof(arity));
            }
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void AllArities_ReturnMatchesWriteBackStateAndMutateComponents(int arity)
        {
            using var world = new World();
            var first = Spawn(world);
            var second = Spawn(world);
            var visitor = new Accumulator { Count = 5 };
            Assert.That(Run(world.Query(), arity, ref visitor), Is.EqualTo(2));
            Assert.That(visitor.Count, Is.EqualTo(7));
            Assert.That(visitor.Last, Is.EqualTo(second));
            foreach (var e in new[] { first, second })
            {
                Assert.That(world.Get<A>(e).Value, Is.EqualTo(arity >= 1 ? 2 : 1));
                Assert.That(world.Get<B>(e).Value, Is.EqualTo(arity >= 2 ? 3 : 2));
                Assert.That(world.Get<C>(e).Value, Is.EqualTo(arity >= 3 ? 4 : 3));
                Assert.That(world.Get<D>(e).Value, Is.EqualTo(arity >= 4 ? 5 : 4));
            }
        }

        [Test]
        public void AllAnyNoneFilters_ArePreservedAndTypedRequirementsAreAdditional()
        {
            using var world = new World();
            var ab = world.CreateEntity(); world.Set(ab, new A()); world.Set(ab, new B());
            var ac = world.CreateEntity(); world.Set(ac, new A()); world.Set(ac, new C());
            var blocked = Spawn(world); world.Set(blocked, new Blocked());
            var noA = world.CreateEntity(); world.Set(noA, new B());
            var query = world.Query().WithAll<A>().WithAny<B>().WithAny<C>().WithNone<Blocked>();
            var visitor = new Accumulator();
            Assert.That(Run(query, 0, ref visitor), Is.EqualTo(2));
            Assert.That(Run(query, 1, ref visitor), Is.EqualTo(2));
            Assert.That(Run(query, 2, ref visitor), Is.EqualTo(1));
            Assert.That(visitor.Count, Is.EqualTo(5));
            Assert.That(visitor.Last, Is.EqualTo(ab));
            Assert.That(query.Count, Is.EqualTo(2));
            Assert.That(world.Get<A>(blocked).Value, Is.EqualTo(1));
        }

        [TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void TypedArities_AutoRequireEveryComponent(int arity)
        {
            using var world = new World();
            for (int missing = 0; missing <= 4; missing++) Spawn(world, missing);
            var visitor = new Accumulator();
            var query = world.Query();
            Assert.That(Run(query, arity, ref visitor), Is.EqualTo(5 - arity));
            Assert.That(visitor.Count, Is.EqualTo(5 - arity));
            Assert.That(query.Count, Is.EqualTo(5));
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void PendingVariants_PreserveFiltersAndExcludeByDefault(int arity)
        {
            using var world = new World();
            var live = Spawn(world);
            var dying = Spawn(world);
            world.RequestDespawn(dying);
            var query = world.Query().WithAny<A>().WithAny<B>();
            var include = query.IncludeDespawning().WithAll<A>().WithNone<Blocked>();
            var only = query.OnlyDespawning().WithAll<A>().WithNone<Blocked>();
            var visitor = new Accumulator();
            Assert.That(Run(query, arity, ref visitor), Is.EqualTo(1));
            Assert.That(visitor.Last, Is.EqualTo(live));
            Assert.That(Run(include, arity, ref visitor), Is.EqualTo(2));
            Assert.That(Run(only, arity, ref visitor), Is.EqualTo(1));
            Assert.That(visitor.Last, Is.EqualTo(dying));
            Assert.That(visitor.Count, Is.EqualTo(4));
            Assert.That(query.Count, Is.EqualTo(1));
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void EmptyAndDisposedQueries_DoNotOverwriteExistingVisitorState(int arity)
        {
            using var world = new World();
            var query = world.Query();
            var visitor = new Accumulator { Count = 8 };
            Assert.That(Run(query, arity, ref visitor), Is.Zero);
            Assert.That(visitor.Count, Is.EqualTo(8));
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => Run(query, arity, ref visitor));
            Assert.That(visitor.Count, Is.EqualTo(8));
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void CallbackException_WritesBackPartialStateAndReleasesGuard(int arity)
        {
            using var world = new World();
            var first = Spawn(world);
            var second = Spawn(world);
            var untouched = Spawn(world);
            var visitor = new Accumulator { ThrowAt = 2 };
            var query = world.Query();
            Assert.Throws<ApplicationException>(() => Run(query, arity, ref visitor));
            Assert.That(visitor.Count, Is.EqualTo(2));
            Assert.That(visitor.Last, Is.EqualTo(second));
            Assert.That(world.Get<A>(first).Value, Is.EqualTo(arity == 0 ? 1 : 2));
            Assert.That(world.Get<A>(second).Value, Is.EqualTo(arity == 0 ? 1 : 2));
            Assert.That(world.Get<A>(untouched).Value, Is.EqualTo(1));
            Assert.DoesNotThrow(() => world.CreateEntity());
        }

        private struct StructuralVisitor : IEntityVisitor
        {
            public World World;
            public int Operation;
            public int Count;
            public void Visit(Entity e)
            {
                Count++;
                switch (Operation)
                {
                    case 0: World.CreateEntity(); break;
                    case 1: World.DestroyEntity(e); break;
                    case 2: World.Remove<A>(e); break;
                    case 3: World.Set(e, new Blocked()); break;
                    case 4: World.Dispose(); break;
                }
            }
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void StructuralMutation_IsRejectedWithoutLeakingIterationGuard(int operation)
        {
            using var world = new World();
            var e = Spawn(world);
            var query = world.Query();
            var visitor = new StructuralVisitor { World = world, Operation = operation };
            Assert.Throws<InvalidOperationException>(() => query.ForEach(ref visitor));
            Assert.That(visitor.Count, Is.EqualTo(1));
            Assert.That(world.IsAlive(e), Is.True);
            Assert.That(world.Has<A>(e), Is.True);
            Assert.That(world.Has<Blocked>(e), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(1));
            Assert.DoesNotThrow(() => world.CreateEntity());
        }

        private struct NestedVisitor : IEntityVisitor<A>
        {
            public Query Inner;
            public int Count;
            public int InnerMatches;
            public int InnerState;
            public void Visit(Entity e, ref A a)
            {
                Count++;
                var innerVisitor = new Accumulator(); // Never reuse the outer visitor for nested execution.
                InnerMatches += Inner.ForEach<Accumulator, A>(ref innerVisitor);
                InnerState += innerVisitor.Count;
            }
        }

        [Test]
        public void NestedQueries_WithDistinctVisitorsRetainIndependentState()
        {
            using var world = new World();
            var first = Spawn(world); var second = Spawn(world);
            var query = world.Query();
            var visitor = new NestedVisitor { Inner = query };
            Assert.That(query.ForEach<NestedVisitor, A>(ref visitor), Is.EqualTo(2));
            Assert.That(visitor.Count, Is.EqualTo(2));
            Assert.That(visitor.InnerMatches, Is.EqualTo(4));
            Assert.That(visitor.InnerState, Is.EqualTo(4));
            Assert.That(world.Get<A>(first).Value, Is.EqualTo(3));
            Assert.That(world.Get<A>(second).Value, Is.EqualTo(3));
            Assert.DoesNotThrow(() => world.CreateEntity());
        }

        [Test]
        public void CachedQuery_SeesLaterComponentAndLifecycleChanges()
        {
            using var world = new World();
            var query = world.Query().WithAll<A>();
            var visitor = new Accumulator();
            Assert.That(Run(query, 1, ref visitor), Is.Zero);
            var e = Spawn(world);
            Assert.That(Run(query, 1, ref visitor), Is.EqualTo(1));
            world.Remove<A>(e);
            Assert.That(Run(query, 1, ref visitor), Is.Zero);
            world.Set(e, new A());
            Assert.That(Run(query, 1, ref visitor), Is.EqualTo(1));
            world.RequestDespawn(e);
            Assert.That(Run(query, 1, ref visitor), Is.Zero);
            Assert.That(Run(query.IncludeDespawning(), 1, ref visitor), Is.EqualTo(1));
            world.ProcessDespawns();
            Assert.That(Run(query, 1, ref visitor), Is.Zero);
            Assert.That(visitor.Count, Is.EqualTo(3));
        }

        private struct CascadeVisitor : IEntityVisitor<A>
        {
            public World World;
            public Entity Child;
            public Entity Last;
            public int Count;
            public void Visit(Entity e, ref A a) { Count++; Last = e; World.RequestDespawn(Child); }
        }

        private sealed class CleanupSystem : ISystem
        {
            public Query Query;
            public Entity Child;
            public readonly List<Entity> Seen = new List<Entity>();
            public readonly List<int> Counts = new List<int>();
            public void Update(World world, CommandBuffer commands, in EcsTime time)
            {
                var visitor = new CascadeVisitor { World = world, Child = Child };
                Counts.Add(Query.ForEach<CascadeVisitor, A>(ref visitor));
                Counts.Add(visitor.Count);
                Seen.Add(visitor.Last);
            }
        }

        [Test]
        public void OnlyDespawning_UsesCapturedBatchForStructVisitors()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            var first = Spawn(world); var child = Spawn(world);
            var cleanup = new CleanupSystem { Query = world.Query().OnlyDespawning().WithAll<A>(), Child = child };
            loop.Add(EcsPhase.Despawn, cleanup);
            world.RequestDespawn(first);
            Assert.That(loop.RunFrame(0), Is.Zero);
            Assert.That(cleanup.Counts, Is.EqualTo(new[] { 1, 1, 1, 1 }));
            Assert.That(cleanup.Seen, Is.EqualTo(new[] { first, child }));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
        public void WarmStructVisitors_AllocateNothingAcrossTenThousandEntities(int arity)
        {
            using var world = new World(10000);
            for (int i = 0; i < 10000; i++) Spawn(world);
            var query = world.Query().WithAll<A>().WithNone<Blocked>();
            int matches = 0, visits = 0;
            TestDelegate body = () =>
            {
                var visitor = new Accumulator();
                matches = Run(query, arity, ref visitor);
                visits = visitor.Count;
            };
            for (int i = 0; i < 8; i++) body(); // Warm the exact delegate and generic path being measured.
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory()); // Uses Unity's GC.Alloc recorder, not Boehm's dead counter.
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
#endif
            Assert.That(matches, Is.EqualTo(10000));
            Assert.That(visits, Is.EqualTo(10000));
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
