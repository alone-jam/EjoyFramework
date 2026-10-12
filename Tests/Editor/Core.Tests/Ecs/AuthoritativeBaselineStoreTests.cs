using System;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class AuthoritativeBaselineStoreTests
    {
        private struct State { public int Value; }

        private static Entity Spawn(World world, int value = 0)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new State { Value = value });
            return entity;
        }

        [Test]
        public void WritesAreCopiedAndStrictlyOrdered_WithoutChangingWorld()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world, 99);
            var binding = store.Bind(entity);
            var input = new State { Value = 7 };
            Assert.That(store.TryGet(in binding, out _, out _), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TrySet(in binding, 0, in input));
            Assert.That(store.TrySet(in binding, 10, in input), Is.EqualTo(BaselineWriteResult.Accepted));
            input.Value = 8;
            Assert.That(store.TrySet(in binding, 10, in input), Is.EqualTo(BaselineWriteResult.DuplicateTick));
            Assert.That(store.TrySet(in binding, 9, in input), Is.EqualTo(BaselineWriteResult.OlderTick));
            Assert.That(store.TryGet(in binding, out uint tick, out State output), Is.True);
            Assert.That(tick, Is.EqualTo(10));
            Assert.That(output.Value, Is.EqualTo(7));
            output.Value = 100;
            store.TryGet(in binding, out _, out var again);
            Assert.That(again.Value, Is.EqualTo(7));
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(99));
            Assert.That(store.TrySet(in binding, uint.MaxValue, in input), Is.EqualTo(BaselineWriteResult.Accepted));
            Assert.That(store.TrySet(in binding, 1, in input), Is.EqualTo(BaselineWriteResult.OlderTick));
        }

        [Test]
        public void RebindInvalidatesOldTokenIncludingUnbind_AndResetInvalidatesTimeline()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world);
            var old = store.Bind(entity);
            var value = new State { Value = 1 };
            store.TrySet(in old, 100, in value);
            var current = store.Bind(entity);
            Assert.That(store.TrySet(in old, 101, in value), Is.EqualTo(BaselineWriteResult.StaleBinding));
            Assert.That(store.Unbind(in old), Is.False);
            Assert.That(store.TryGet(in current, out _, out _), Is.False);
            Assert.That(store.TrySet(in current, 2, in value), Is.EqualTo(BaselineWriteResult.Accepted));
            store.ResetTimeline();
            var reset = store.Bind(entity);
            Assert.That(store.TrySet(in current, 1000, in value), Is.EqualTo(BaselineWriteResult.StaleBinding));
            Assert.That(store.TrySet(in reset, 1, in value), Is.EqualTo(BaselineWriteResult.Accepted));
            Assert.That(store.Unbind(in reset), Is.True);
            Assert.That(store.Unbind(in reset), Is.False);
        }

        [Test]
        public void BindRequiresCorrectLiveComponent_AndUnavailableWritesPreserveState()
        {
            using var world = new World();
            using var other = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world);
            var value = new State { Value = 12 };
            var binding = store.Bind(entity);
            store.TrySet(in binding, 1, in value);
            Assert.Throws<InvalidOperationException>(() => store.Bind(default));
            Assert.Throws<InvalidOperationException>(() => store.Bind(Spawn(other)));
            var missing = world.CreateEntity();
            Assert.Throws<InvalidOperationException>(() => store.Bind(missing));
            Assert.That(store.TryGet(in binding, out _, out var stored), Is.True);
            Assert.That(stored.Value, Is.EqualTo(12));
            world.RequestDespawn(entity);
            Assert.Throws<InvalidOperationException>(() => store.Bind(entity));
            Assert.That(store.TrySet(in binding, 2, in value), Is.EqualTo(BaselineWriteResult.TargetUnavailable));
            Assert.That(store.TryGet(in binding, out var tick, out stored), Is.False);
            Assert.That(tick, Is.Zero);
            Assert.That(stored.Value, Is.Zero);
            Assert.That(store.Unbind(in binding), Is.True, "目标不可用不阻止释放其绑定");
        }

        [Test]
        public void RecycledEntityIndexAndOtherStoreCannotReuseBindings()
        {
            using var world = new World(1);
            using var store = new AuthoritativeBaselineStore<State>(world, 1);
            using var other = new AuthoritativeBaselineStore<State>(world, 1);
            var first = Spawn(world);
            var old = store.Bind(first);
            var value = new State { Value = 5 };
            world.DestroyEntity(first);
            var second = Spawn(world);
            Assert.That(second.Index, Is.EqualTo(first.Index));
            Assert.That(store.TrySet(in old, 1, in value), Is.EqualTo(BaselineWriteResult.TargetUnavailable));
            var current = store.Bind(second);
            Assert.That(store.TrySet(in old, 2, in value), Is.EqualTo(BaselineWriteResult.StaleBinding));
            Assert.That(store.Unbind(in old), Is.False);
            var foreign = other.Bind(second);
            Assert.That(store.TrySet(in foreign, 1, in value), Is.EqualTo(BaselineWriteResult.StaleBinding));
            Assert.That(store.TrySet(in current, 1, in value), Is.EqualTo(BaselineWriteResult.Accepted));
        }

        [Test]
        public void ComponentRemovalAndReaddition_AutomaticallyInvalidateAccessUntilFreshBind()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world);
            var old = store.Bind(entity);
            var value = new State { Value = 1 };
            store.TrySet(in old, 1, in value);
            world.Remove<State>(entity);
            Assert.That(store.TrySet(in old, 2, in value), Is.EqualTo(BaselineWriteResult.TargetUnavailable));
            Assert.That(store.TryGet(in old, out _, out _), Is.False);
            world.Set(entity, new State()); // No Unbind notification: the membership stamp must detect replacement.
            Assert.That(store.TryGet(in old, out uint tick, out State missing), Is.False);
            Assert.That(tick, Is.Zero); Assert.That(missing.Value, Is.Zero);
            Assert.That(store.TrySet(in old, 1, in value), Is.EqualTo(BaselineWriteResult.TargetUnavailable));
            Assert.That(store.TrySet(in old, 2, in value), Is.EqualTo(BaselineWriteResult.TargetUnavailable));
            var current = store.Bind(entity);
            Assert.That(store.TrySet(in old, uint.MaxValue, in value), Is.EqualTo(BaselineWriteResult.StaleBinding));
            Assert.That(store.Unbind(in old), Is.False);
            Assert.That(store.TryGet(in current, out _, out _), Is.False);
            Assert.That(store.TrySet(in current, 1, in value), Is.EqualTo(BaselineWriteResult.Accepted));
        }

        [Test]
        public void Unbind_RemainsValidAfterComponentDisappears()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world); var binding = store.Bind(entity);
            world.Remove<State>(entity);
            Assert.That(store.Unbind(in binding), Is.True);
            Assert.That(store.Unbind(in binding), Is.False);
        }

        [Test]
        public void ValueWritesSwapRemovalAndReservation_DoNotInvalidateBinding()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var removed = Spawn(world); var entity = Spawn(world);
            var binding = store.Bind(entity); var value = new State { Value = 42 };
            store.TrySet(in binding, 7, in value);
            world.Remove<State>(removed); // The bound component moves within the dense array.
            world.EnsureCapacity<State>(256);
            world.Set(entity, new State { Value = 99 });
            world.Get<State>(entity).Value = 100;
            Assert.That(store.TryGet(in binding, out uint tick, out State output), Is.True);
            Assert.That(tick, Is.EqualTo(7)); Assert.That(output.Value, Is.EqualTo(42));
            Assert.That(store.TrySet(in binding, 8, in value), Is.EqualTo(BaselineWriteResult.Accepted));
        }

        [Test]
        public void WarmReplacedMembershipValidation_AllocatesNothing()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world); var binding = store.Bind(entity); var value = new State { Value = 5 };
            store.TrySet(in binding, 1, in value);
            world.Remove<State>(entity); world.Set(entity, new State());
            int rejected = 0;
            TestDelegate body = () =>
            {
                rejected = 0;
                for (int i = 0; i < 1000; i++)
                {
                    if (store.TrySet(in binding, 2, in value) == BaselineWriteResult.TargetUnavailable) rejected++;
                    if (!store.TryGet(in binding, out uint tick, out State output) && tick == 0 && output.Value == 0) rejected++;
                }
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(rejected, Is.EqualTo(2000));
        }

        [Test]
        public void GrowthAndExplicitCapacityPreserveBindingsAndValues()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world, 0);
            var low = store.Bind(Spawn(world));
            var value = new State { Value = 42 };
            store.TrySet(in low, 1, in value);
            for (int i = 0; i < 100; i++) world.CreateEntity();
            var high = store.Bind(Spawn(world));
            store.TrySet(in high, 2, in value);
            store.EnsureCapacity(512);
            store.EnsureCapacity(1);
            Assert.That(store.Capacity, Is.GreaterThanOrEqualTo(512));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.EnsureCapacity(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.EnsureCapacity(int.MaxValue));
            Assert.That(store.TryGet(in low, out uint tick, out State data), Is.True);
            Assert.That(tick, Is.EqualTo(1));
            Assert.That(data.Value, Is.EqualTo(42));
            Assert.That(store.TryGet(in high, out tick, out _), Is.True);
            Assert.That(tick, Is.EqualTo(2));
        }

        [Test]
        public void MutationRequiresSafePoint_ReadsRemainCopies_AndDisposeDoesNotOwnWorld()
        {
            using var world = new World();
            var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world);
            var binding = store.Bind(entity);
            var value = new State { Value = 3 };
            store.TrySet(in binding, 1, in value);
            world.Query().ForEach(e =>
            {
                Assert.Throws<InvalidOperationException>(() => store.Bind(e));
                Assert.Throws<InvalidOperationException>(() => store.TrySet(in binding, 2, in value));
                Assert.Throws<InvalidOperationException>(() => store.Unbind(in binding));
                Assert.Throws<InvalidOperationException>(() => store.ResetTimeline());
                Assert.Throws<InvalidOperationException>(() => store.EnsureCapacity(0));
                Assert.Throws<InvalidOperationException>(() => store.Dispose());
                Assert.That(store.TryGet(in binding, out _, out var copy), Is.True);
                Assert.That(copy.Value, Is.EqualTo(3));
            });
            store.Dispose();
            Assert.That(world.IsDisposed, Is.False);
            Assert.Throws<ObjectDisposedException>(() => store.TryGet(in binding, out _, out _));
            using var afterWorld = new AuthoritativeBaselineStore<State>(world);
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => afterWorld.ResetTimeline());
            Assert.DoesNotThrow(() => afterWorld.Dispose());
        }

        [Test]
        public void ForeignThreadCannotReadOrWrite()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var binding = store.Bind(Spawn(world));
            var value = new State();
            Exception read = null, write = null;
            var worker = new Thread(() =>
            {
                try { store.TryGet(in binding, out _, out _); } catch (Exception ex) { read = ex; }
                try { store.TrySet(in binding, 1, in value); } catch (Exception ex) { write = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            Assert.That(read, Is.TypeOf<InvalidOperationException>());
            Assert.That(write, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void WarmReadWriteUnbindAndRebindAllocateNothing()
        {
            using var world = new World();
            using var store = new AuthoritativeBaselineStore<State>(world);
            var entity = Spawn(world);
            var value = new State { Value = 1 };
            int accepted = 0;
            TestDelegate body = () =>
            {
                accepted = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var binding = store.Bind(entity);
                    if (store.TrySet(in binding, 1, in value) == BaselineWriteResult.Accepted) accepted++;
                    if (store.TryGet(in binding, out _, out var copy) && copy.Value == 1) accepted++;
                    if (store.Unbind(in binding)) accepted++;
                }
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(accepted, Is.EqualTo(3000));
        }
    }
}
