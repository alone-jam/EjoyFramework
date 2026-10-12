using System;
using System.Reflection;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class ComponentLifetimeTests
    {
        private struct State { public int Value; }
        private struct Missing { }

        private static ulong Lifetime(World world, Entity entity)
        {
            Assert.That(world.TryGetComponentLifetime<State>(entity, out ulong lifetime), Is.True);
            Assert.That(lifetime, Is.Not.Zero);
            return lifetime;
        }

        [Test]
        public void Stamp_IsStableAcrossValueWritesCapacityAndPendingDespawn_AndReadableDuringIteration()
        {
            using var world = new World(1);
            var e = world.CreateEntity(); world.Set(e, new State { Value = 1 });
            ulong stamp = Lifetime(world, e);
            world.Set(e, new State { Value = 2 });
            world.Get<State>(e).Value = 3;
            world.EnsureEntityCapacity(128); world.EnsureCapacity<State>(128);
            Assert.That(Lifetime(world, e), Is.EqualTo(stamp));
            world.Query().ForEach(entity => Assert.That(Lifetime(world, entity), Is.EqualTo(stamp)));
            world.RequestDespawn(e);
            Assert.That(Lifetime(world, e), Is.EqualTo(stamp));
            world.ProcessDespawns();
            Assert.That(world.TryGetComponentLifetime<State>(e, out stamp), Is.False);
            Assert.That(stamp, Is.Zero);
        }

        [Test]
        public void SwapRemovalReadditionAndGrowth_PreserveEachRemainingMembershipStamp()
        {
            using var world = new World(1);
            world.EnsureCapacity<State>(3);
            var entities = new Entity[65]; var stamps = new ulong[65];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.CreateEntity(); world.Set(entities[i], new State { Value = i });
                stamps[i] = Lifetime(world, entities[i]);
                if (i != 0) Assert.That(stamps[i], Is.GreaterThan(stamps[i - 1]));
            }
            world.Remove<State>(entities[10]);
            Assert.That(world.TryGetComponentLifetime<State>(entities[10], out var missing), Is.False);
            Assert.That(missing, Is.Zero);
            Assert.That(Lifetime(world, entities[64]), Is.EqualTo(stamps[64]));
            world.Set(entities[10], new State { Value = 10 });
            Assert.That(Lifetime(world, entities[10]), Is.GreaterThan(stamps[64]));
            stamps[10] = Lifetime(world, entities[10]);
            world.EnsureCapacity<State>(256);
            for (int i = 0; i < entities.Length; i++) Assert.That(Lifetime(world, entities[i]), Is.EqualTo(stamps[i]));
            world.DestroyEntity(entities[20]);
            var reused = world.CreateEntity(); world.Set(reused, new State());
            Assert.That(reused.Index, Is.EqualTo(entities[20].Index));
            Assert.That(Lifetime(world, reused), Is.GreaterThan(stamps[10]));
            Assert.That(world.TryGetComponentLifetime<State>(entities[20], out _), Is.False);
        }

        [Test]
        public void MissingForeignStaleAndReservedHandles_ReturnFalseAndZero_DisposedThrows()
        {
            using var world = new World(); using var foreign = new World();
            using var commands = new CommandBuffer(world);
            var live = world.CreateEntity(); var stale = world.CreateEntity(); world.DestroyEntity(stale);
            foreach (var entity in new[] { default(Entity), live, stale, foreign.CreateEntity(), commands.CreateEntity() })
            {
                ulong stamp = 99;
                Assert.That(world.TryGetComponentLifetime<State>(entity, out stamp), Is.False);
                Assert.That(stamp, Is.Zero);
            }
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.TryGetComponentLifetime<State>(live, out _));
        }

        [Test]
        public void MissingLifetimeProbe_DoesNotCreatePerWorldStorage()
        {
            using var warm = new World(); using var target = new World();
            World active = warm; Entity entity = warm.CreateEntity();
            var targetEntity = target.CreateEntity();
            bool found = true; ulong stamp = 99;
            TestDelegate body = () => found = active.TryGetComponentLifetime<Missing>(entity, out stamp);
            body(); // Initialize the generic type ID without probing the target world.
            active = target; entity = targetEntity;
            EcsAllocationTests.AssertZero(body);
            Assert.That(found, Is.False); Assert.That(stamp, Is.Zero);
            Assert.That(target.EntityCount, Is.EqualTo(1));
        }

        [Test]
        public void Exhaustion_AllowsLastStampOnce_AndRejectsBeforeArrayOrLogicalMutation()
        {
            using var world = new World();
            var first = world.CreateEntity(); world.Set(first, new State());
            object pool = FindPool(world);
            var counter = pool.GetType().GetField("m_LastLifetime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(counter, Is.Not.Null);
            counter.SetValue(pool, ulong.MaxValue - 1); // Test-only instance-local boundary setup.
            var last = world.CreateEntity(); world.Set(last, new State { Value = 7 });
            Assert.That(Lifetime(world, last), Is.EqualTo(ulong.MaxValue));
            world.Set(last, new State { Value = 8 });
            world.EnsureCapacity<State>(128);
            Assert.That(Lifetime(world, last), Is.EqualTo(ulong.MaxValue));
            world.Remove<State>(last);
            pool.GetType().GetMethod("Clear").Invoke(pool, null);
            Assert.That((ulong)counter.GetValue(pool), Is.EqualTo(ulong.MaxValue));
            string[] names = { "m_Sparse", "m_Entities", "m_Data", "m_Lifetimes" };
            var before = new object[names.Length];
            for (int i = 0; i < names.Length; i++) before[i] = Field(pool, names[i]);
            Assert.Throws<OverflowException>(() => world.Set(first, new State { Value = 9 }));
            Assert.That(world.Has<State>(first), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(2));
            for (int i = 0; i < names.Length; i++) Assert.That(Field(pool, names[i]), Is.SameAs(before[i]));
        }

        [Test]
        public void SwapRemoval_ZeroesVacatedLifetimeTail()
        {
            using var world = new World();
            var first = world.CreateEntity(); var second = world.CreateEntity();
            world.Set(first, new State()); world.Set(second, new State());
            ulong secondStamp = Lifetime(world, second);
            world.Remove<State>(first);
            var lifetimes = (ulong[])Field(FindPool(world), "m_Lifetimes");
            Assert.That(lifetimes[0], Is.EqualTo(secondStamp));
            Assert.That(lifetimes[1], Is.Zero);
        }

        private static object FindPool(World world)
        {
            var method = typeof(World).GetMethod("FindPool", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method.MakeGenericMethod(typeof(State)).Invoke(world, null);
        }

        private static object Field(object pool, string name)
        {
            var field = pool.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(pool);
        }

        [Test]
        public void WarmLifetimeLookups_AllocateNothing()
        {
            using var world = new World();
            var entity = world.CreateEntity(); world.Set(entity, new State());
            ulong expected = Lifetime(world, entity);
            int matches = 0;
            TestDelegate body = () =>
            {
                matches = 0;
                for (int i = 0; i < 10000; i++)
                    if (world.TryGetComponentLifetime<State>(entity, out ulong stamp) && stamp == expected) matches++;
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(matches, Is.EqualTo(10000));
        }
    }
}
