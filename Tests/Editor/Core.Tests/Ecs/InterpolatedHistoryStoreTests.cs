using System;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class InterpolatedHistoryStoreTests
    {
        private struct State : IInterpolated<State>
        {
            public double Value;
            public float Weight;
            public bool Interpolated;
            public bool ThrowOnInterpolate;

            public State Interpolate(in State to, float alpha)
            {
                var result = new State
                {
                    Value = Value + (to.Value - Value) * alpha,
                    Weight = alpha,
                    Interpolated = true
                };
                Value = -999; // Deliberately mutating receiver: storage must be insulated.
                if (ThrowOnInterpolate) throw new InvalidOperationException("Interpolation failed.");
                return result;
            }
        }

        private static Entity Spawn(World world, double value = 0)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new State { Value = value });
            return entity;
        }

        private static void Add(InterpolatedHistoryStore<State> store,
            InterpolatedHistoryStore<State>.Binding binding, uint tick, double value)
        {
            var state = new State { Value = value };
            Assert.That(store.TryAdd(in binding, tick, in state), Is.EqualTo(HistoryWriteResult.Accepted));
        }

        private static void Sample(InterpolatedHistoryStore<State> store,
            InterpolatedHistoryStore<State>.Binding binding, uint tick, double fraction, double expected)
        {
            Assert.That(store.TrySample(in binding, tick, fraction, out var state), Is.True);
            Assert.That(state.Value, Is.EqualTo(expected).Within(0.00002));
        }

        [Test]
        public void EmptyAndDefaultBindingReturnFalseAndDefault()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 0, historyLength: 3);
            Assert.That(store.Capacity, Is.Zero);
            Assert.That(store.HistoryLength, Is.EqualTo(3));
            Assert.That(store.TrySample(default, 0, 0, out var empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
            var binding = store.Bind(Spawn(world));
            Assert.That(binding.Entity, Is.Not.EqualTo(default(Entity)));
            Assert.That(store.TrySample(in binding, uint.MaxValue, 0.5, out empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
            var state = new State();
            Assert.That(store.TryAdd(default, 1, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.Unbind(default), Is.False);
        }

        [TestCase(0u, 0.0)]
        [TestCase(0u, 0.75)]
        [TestCase(9u, 0.99)]
        [TestCase(10u, 0.0)]
        [TestCase(10u, 0.5)]
        [TestCase(uint.MaxValue, 0.99)]
        public void SingleSampleIsReturnedDirectlyAtAnyTime(uint target, double fraction)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 3);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 10, 42);
            Assert.That(store.TrySample(in binding, target, fraction, out var result), Is.True);
            Assert.That(result.Value, Is.EqualTo(42));
            Assert.That(result.Interpolated, Is.False);
        }

        [TestCase(0u, 0.99, 11.0)]
        [TestCase(9u, 0.99, 11.0)]
        [TestCase(10u, 0.0, 11.0)]
        [TestCase(30u, 0.0, 77.0)]
        [TestCase(100u, 0.0, -20.0)]
        [TestCase(100u, 0.99, -20.0)]
        [TestCase(uint.MaxValue, 0.99, -20.0)]
        public void ExactTicksAndEndClampsDoNotInvokeInterpolation(uint target, double fraction, double expected)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 3);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 10, 11);
            Add(store, binding, 30, 77);
            Add(store, binding, 100, -20);
            Assert.That(store.TrySample(in binding, target, fraction, out var result), Is.True);
            Assert.That(result.Value, Is.EqualTo(expected));
            Assert.That(result.Interpolated, Is.False);
        }

        [TestCase(10u, 0.5, 0.005, 11.0)]
        [TestCase(60u, 0.25, 0.5025, 110.5)]
        [TestCase(109u, 0.5, 0.995, 209.0)]
        public void SparseSamplesUseActualTickDistanceNotClockFraction(uint target, double fraction,
            double weight, double expected)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 2);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 10, 10);
            Add(store, binding, 110, 210);
            Assert.That(store.TrySample(in binding, target, fraction, out var result), Is.True);
            Assert.That(result.Interpolated, Is.True);
            Assert.That(result.Weight, Is.EqualTo(weight).Within(0.000001));
            Assert.That(result.Weight, Is.InRange(0f, 1f));
            Assert.That(result.Value, Is.EqualTo(expected).Within(0.00002));
        }

        [Test]
        public void WideTickArithmeticAndFractionsNearUintMaxDoNotWrap()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 2);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 1, 0);
            Add(store, binding, uint.MaxValue, 100);
            Sample(store, binding, 2147483648u, 0, 50);
            Sample(store, binding, uint.MaxValue - 1, 0.5, 100);
            binding = store.Bind(binding.Entity);
            Add(store, binding, uint.MaxValue - 1, 0);
            Add(store, binding, uint.MaxValue, 8);
            Sample(store, binding, uint.MaxValue - 1, 0.25, 2);
            Assert.That(store.TrySample(in binding, uint.MaxValue - 1, 0.000000001, out var tiny), Is.True);
            Assert.That(tiny.Weight, Is.EqualTo(0.000000001).Within(0.000000000000001));
            var state = new State { Value = 999 };
            Assert.That(store.TryAdd(in binding, 1, in state), Is.EqualTo(HistoryWriteResult.OutsideWindow));
            Sample(store, binding, 0, 0, 0);
            Sample(store, binding, uint.MaxValue, 0.99, 8);
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(-double.Epsilon)]
        [TestCase(1.0)]
        [TestCase(1.5)]
        public void InvalidFractionIsRejectedEvenForEmptyHistory(double fraction)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TrySample(in binding, 0, fraction, out _));
            Add(store, binding, 1, 8);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TrySample(in binding, 1, fraction, out _));
            Sample(store, binding, 1, 0, 8);
        }

        [Test]
        public void ZeroStoredTickIsRejectedWithoutChangingHistory()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            var state = new State { Value = 999 };
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TryAdd(in binding, 0, in state));
            Assert.That(store.TrySample(in binding, 0, 0, out _), Is.False);
            Add(store, binding, 1, 8);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TryAdd(in binding, 0, in state));
            Sample(store, binding, 0, 0, 8);
        }

        [Test]
        public void OutOfOrderSamplesFillRoomThenRetainNewestWindow_FirstDuplicateWins()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 4);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 30, 30);
            Add(store, binding, 10, 10);
            Add(store, binding, 20, 20);
            Add(store, binding, 1, 1);
            var duplicate = new State { Value = 999 };
            foreach (uint tick in new uint[] { 1, 10, 20, 30 })
                Assert.That(store.TryAdd(in binding, tick, in duplicate), Is.EqualTo(HistoryWriteResult.DuplicateTick));
            Add(store, binding, 25, 25); // Older than baseline's latest, but inside the retained window.
            Sample(store, binding, 0, 0, 10);
            Sample(store, binding, 20, 0, 20);
            Sample(store, binding, 25, 0, 25);
            Sample(store, binding, 30, 0, 30);
            Add(store, binding, 40, 40);
            Assert.That(store.TryAdd(in binding, 10, in duplicate), Is.EqualTo(HistoryWriteResult.OutsideWindow));
            Assert.That(store.TryAdd(in binding, 19, in duplicate), Is.EqualTo(HistoryWriteResult.OutsideWindow));
            Assert.That(store.TryAdd(in binding, 20, in duplicate), Is.EqualTo(HistoryWriteResult.DuplicateTick));
            Sample(store, binding, 0, 0, 20);
            Sample(store, binding, 27, 0.5, 27.5);
            Sample(store, binding, 40, 0, 40);
        }

        [TestCase(15u, 15.0)]
        [TestCase(25u, 20.0)]
        [TestCase(35u, 20.0)]
        public void FullWindowInsertionPreservesSuffixAndEvictsExactlyOneOldest(uint tick, double oldest)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 3);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 10, 10);
            Add(store, binding, 20, 20);
            Add(store, binding, 30, 30);
            Add(store, binding, tick, tick);
            Sample(store, binding, 0, 0, oldest);
            Sample(store, binding, tick, 0, tick);
            Sample(store, binding, 20, 0, 20);
            Sample(store, binding, 30, 0, 30);
            var duplicate = new State { Value = 999 };
            Assert.That(store.TryAdd(in binding, 30, in duplicate), Is.EqualTo(HistoryWriteResult.DuplicateTick));
        }

        [Test]
        public void LengthOneRejectsOlderAndReplacesOnlyWithNewer()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 1);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 100, 1);
            var state = new State { Value = 2 };
            Assert.That(store.TryAdd(in binding, 100, in state), Is.EqualTo(HistoryWriteResult.DuplicateTick));
            Assert.That(store.TryAdd(in binding, 99, in state), Is.EqualTo(HistoryWriteResult.OutsideWindow));
            Sample(store, binding, 100, 0, 1);
            Add(store, binding, 101, 2);
            Sample(store, binding, 0, 0, 2);
        }

        [Test]
        public void InputsOutputsAndMutatingInterpolationReceiverCannotChangeStoredSamplesOrWorld()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var entity = Spawn(world, 123);
            var binding = store.Bind(entity);
            var input = new State { Value = 10 };
            Assert.That(store.TryAdd(in binding, 10, in input), Is.EqualTo(HistoryWriteResult.Accepted));
            input.Value = 999;
            Add(store, binding, 20, 20);
            for (int i = 0; i < 3; i++)
            {
                Sample(store, binding, 15, 0, 15);
                Assert.That(store.TrySample(in binding, 10, 0, out var output), Is.True);
                Assert.That(output.Value, Is.EqualTo(10));
                output.Value = -1;
                Sample(store, binding, 20, 0, 20);
            }
            Assert.That(input.Value, Is.EqualTo(999));
            Assert.That(world.Get<State>(entity).Value, Is.EqualTo(123));
        }

        [Test]
        public void ThrowingInterpolatorPreservesBothSamples()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            var state = new State { Value = 10, ThrowOnInterpolate = true };
            Assert.That(store.TryAdd(in binding, 10, in state), Is.EqualTo(HistoryWriteResult.Accepted));
            Add(store, binding, 20, 20);
            Assert.Throws<InvalidOperationException>(() => store.TrySample(in binding, 15, 0, out _));
            Sample(store, binding, 10, 0, 10);
            Sample(store, binding, 20, 0, 20);
        }

        [Test]
        public void RebindAndTimelineResetInvalidateQueuedTokensWithoutClearingReplacement()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 32, historyLength: 2);
            var entity = Spawn(world);
            var old = store.Bind(entity);
            Add(store, old, 100, 100);
            var current = store.Bind(entity);
            Assert.That(store.TrySample(in current, 100, 0, out _), Is.False);
            Add(store, current, 1, 1);
            var state = new State { Value = 999 };
            Assert.That(store.TryAdd(in old, uint.MaxValue, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.TrySample(in old, 100, 0, out var empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
            Assert.That(store.Unbind(in old), Is.False);
            Sample(store, current, 1, 0, 1);
            var other = store.Bind(Spawn(world));
            Add(store, other, 50, 50);
            store.ResetTimeline();
            Assert.That(store.Capacity, Is.EqualTo(32));
            Assert.That(store.HistoryLength, Is.EqualTo(2));
            Assert.That(store.TrySample(in other, 50, 0, out _), Is.False);
            var reset = store.Bind(entity);
            Assert.That(store.TrySample(in reset, 1, 0, out _), Is.False);
            Add(store, reset, 1, 2);
            Assert.That(store.TryAdd(in current, uint.MaxValue, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.Unbind(in current), Is.False);
            Sample(store, reset, 1, 0, 2);
            Assert.That(store.Unbind(in reset), Is.True);
            Assert.That(store.Unbind(in reset), Is.False);
            Assert.That(store.TrySample(in reset, 1, 0, out empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
        }

        [Test]
        public void OtherStoreAndReusedEntitySlotsCannotReuseTokens()
        {
            using var world = new World(1);
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 1);
            using var other = new InterpolatedHistoryStore<State>(world);
            var first = Spawn(world);
            var old = store.Bind(first);
            Add(store, old, 10, 10);
            world.DestroyEntity(first);
            var next = Spawn(world);
            Assert.That(next.Index, Is.EqualTo(first.Index));
            var state = new State { Value = 999 };
            Assert.That(store.TryAdd(in old, 11, in state), Is.EqualTo(HistoryWriteResult.TargetUnavailable));
            Assert.That(store.TrySample(in old, 10, 0, out _), Is.False);
            var current = store.Bind(next);
            Add(store, current, 1, 1);
            Assert.That(store.TryAdd(in old, 12, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.Unbind(in old), Is.False);
            var foreign = other.Bind(next);
            Assert.That(store.TryAdd(in foreign, 1, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.TrySample(in foreign, 1, 0, out _), Is.False);
            Assert.That(store.Unbind(in foreign), Is.False);
            Sample(store, current, 1, 0, 1);
        }

        [Test]
        public void BindRequiresLiveLocalComponentAndRejectsPendingDespawn()
        {
            using var world = new World();
            using var otherWorld = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            Assert.Throws<InvalidOperationException>(() => store.Bind(default));
            Assert.Throws<InvalidOperationException>(() => store.Bind(Spawn(otherWorld)));
            Assert.Throws<InvalidOperationException>(() => store.Bind(world.CreateEntity()));
            var entity = Spawn(world);
            var binding = store.Bind(entity);
            Add(store, binding, 10, 10);
            world.RequestDespawn(entity);
            Assert.Throws<InvalidOperationException>(() => store.Bind(entity));
            var state = new State();
            foreach (uint tick in new uint[] { 1, 10, 11 })
                Assert.That(store.TryAdd(in binding, tick, in state), Is.EqualTo(HistoryWriteResult.TargetUnavailable));
            Assert.That(store.TrySample(in binding, 10, 0, out var empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
            Assert.That(store.Unbind(in binding), Is.True);
        }

        [TestCase(0u, 0.0)]
        [TestCase(10u, 0.0)]
        [TestCase(15u, 0.5)]
        [TestCase(20u, 0.0)]
        [TestCase(uint.MaxValue, 0.0)]
        public void ReplacedComponent_InvalidatesEveryCachedSamplingPathWithoutUnbind(uint targetTick, double fraction)
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var entity = Spawn(world);
            var old = store.Bind(entity);
            Add(store, old, 10, 10);
            Add(store, old, 20, 20);
            world.Remove<State>(entity);
            world.Set(entity, new State()); // No store call between removal and readdition.
            Assert.That(store.TrySample(in old, targetTick, fraction, out var missing), Is.False);
            Assert.That(missing, Is.EqualTo(default(State)));
            var value = new State { Value = 999 };
            Assert.That(store.TryAdd(in old, 30, in value), Is.EqualTo(HistoryWriteResult.TargetUnavailable));
            var current = store.Bind(entity);
            Add(store, current, 1, 1);
            Assert.That(store.Unbind(in old), Is.False);
            Assert.That(store.TrySample(in old, 1, 0, out _), Is.False);
            Sample(store, current, 1, 0, 1);
        }

        [Test]
        public void ExplicitUnbindStillReleasesUnavailableMembershipAndOldQueuedDataStaysStale()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var entity = Spawn(world);
            var old = store.Bind(entity);
            Add(store, old, 10, 10);
            world.Remove<State>(entity);
            var state = new State { Value = 999 };
            Assert.That(store.TryAdd(in old, 11, in state), Is.EqualTo(HistoryWriteResult.TargetUnavailable));
            Assert.That(store.TrySample(in old, 10, 0, out var empty), Is.False);
            Assert.That(empty, Is.EqualTo(default(State)));
            Assert.That(store.Unbind(in old), Is.True);
            world.Set(entity, new State());
            var current = store.Bind(entity);
            Assert.That(store.TrySample(in current, 10, 0, out _), Is.False);
            Add(store, current, 1, 1);
            Assert.That(store.TryAdd(in old, uint.MaxValue, in state), Is.EqualTo(HistoryWriteResult.StaleBinding));
            Assert.That(store.Unbind(in old), Is.False);
            Sample(store, current, 1, 0, 1);
        }

        [Test]
        public void CapacityValidationRejectsProductsBeforeHugeAllocationsAndPreservesHistory()
        {
            using var world = new World();
            Assert.Throws<ArgumentNullException>(() => new InterpolatedHistoryStore<State>(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, historyLength: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, historyLength: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, 1500000000, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InterpolatedHistoryStore<State>(world, 2, int.MaxValue));
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 2, historyLength: 3);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 1, 1);
            Add(store, binding, 3, 3);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.EnsureCapacity(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.EnsureCapacity(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.EnsureCapacity(1000000000));
            Assert.That(store.Capacity, Is.EqualTo(2));
            Sample(store, binding, 2, 0, 2);
            Add(store, binding, 2, 2);
        }

        [Test]
        public void BindGrowthValidatesExclusiveIndexProductBeforeAllocating()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 0, historyLength: 0x7FEFFFFF);
            world.CreateEntity();
            var high = Spawn(world); // Index 1 requires two entity slots, not one live component.
            Assert.That(high.Index, Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Bind(high));
            Assert.That(store.Capacity, Is.Zero);
        }

        [Test]
        public void BindGrowthAndExplicitReservePreserveIndependentEntityRanges()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: 0, historyLength: 3);
            var low = store.Bind(Spawn(world));
            Add(store, low, 10, 10);
            Add(store, low, 20, 20);
            for (int i = 0; i < 100; i++) world.CreateEntity();
            var high = store.Bind(Spawn(world));
            Add(store, high, 10, 100);
            Add(store, high, 20, 200);
            store.EnsureCapacity(512);
            store.EnsureCapacity(0);
            store.EnsureCapacity(1);
            Assert.That(store.Capacity, Is.EqualTo(512));
            Sample(store, low, 15, 0, 15);
            Sample(store, high, 15, 0, 150);
            Assert.That(store.Unbind(in high), Is.True);
            Sample(store, low, 15, 0, 15);
        }

        [Test]
        public void TenThousandEntitiesKeepIndependentHistoryAcrossEvictions()
        {
            const int count = 10000;
            using var world = new World(count);
            world.EnsureCapacity<State>(count);
            using var store = new InterpolatedHistoryStore<State>(world, entityCapacity: count, historyLength: 2);
            var bindings = new InterpolatedHistoryStore<State>.Binding[count];
            for (int i = 0; i < count; i++)
            {
                bindings[i] = store.Bind(Spawn(world));
                Add(store, bindings[i], 2, i + 2);
                Add(store, bindings[i], 1, i + 1);
                Add(store, bindings[i], 3, i + 3);
            }
            for (int i = 0; i < count; i++)
            {
                Sample(store, bindings[i], 0, 0, i + 2);
                Sample(store, bindings[i], 2, 0.5, i + 2.5);
            }
            Assert.That(store.Capacity, Is.EqualTo(count));
        }

        [Test]
        public void IterationAllowsSamplingButRejectsAllMutationIncludingNoOps()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 10, 10);
            Add(store, binding, 20, 20);
            var state = new State { Value = 99 };
            world.Query().ForEach(entity =>
            {
                Assert.Throws<InvalidOperationException>(() => store.Bind(entity));
                Assert.Throws<InvalidOperationException>(() => new InterpolatedHistoryStore<State>(world));
                Assert.Throws<InvalidOperationException>(() => store.TryAdd(in binding, 15, in state));
                Assert.Throws<InvalidOperationException>(() => store.TryAdd(in binding, 20, in state));
                Assert.Throws<InvalidOperationException>(() => store.Unbind(default));
                Assert.Throws<InvalidOperationException>(() => store.Unbind(in binding));
                Assert.Throws<InvalidOperationException>(() => store.ResetTimeline());
                Assert.Throws<InvalidOperationException>(() => store.EnsureCapacity(0));
                Assert.Throws<InvalidOperationException>(() => store.Dispose());
                Assert.That(store.Capacity, Is.GreaterThan(binding.Entity.Index));
                Assert.That(store.HistoryLength, Is.GreaterThanOrEqualTo(1));
                Sample(store, binding, 15, 0, 15);
            });
            Sample(store, binding, 15, 0, 15);
            Add(store, binding, 15, 15);
        }

        private static Action[] Accessors(InterpolatedHistoryStore<State> store,
            InterpolatedHistoryStore<State>.Binding binding)
        {
            var state = new State();
            return new Action[]
            {
                () => { _ = store.Capacity; }, () => { _ = store.HistoryLength; },
                () => store.Bind(binding.Entity), () => store.Unbind(in binding),
                () => store.TryAdd(in binding, 1, in state),
                () => store.TrySample(in binding, 1, 0, out _),
                () => store.EnsureCapacity(0), () => store.ResetTimeline()
            };
        }

        [Test]
        public void EveryAccessorAndDisposeRejectForeignThreadWithoutChangingData()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            Add(store, binding, 1, 1);
            var operations = Accessors(store, binding);
            var errors = new Exception[operations.Length + 1];
            var worker = new Thread(() =>
            {
                for (int i = 0; i < operations.Length; i++)
                    try { operations[i](); } catch (Exception ex) { errors[i] = ex; }
                try { store.Dispose(); } catch (Exception ex) { errors[operations.Length] = ex; }
            }) { IsBackground = true };
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            foreach (var error in errors) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Sample(store, binding, 1, 0, 1);
        }

        [Test]
        public void DisposalRejectsAccessIsIdempotentAndDoesNotOwnWorld()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world);
            var binding = store.Bind(Spawn(world));
            store.Dispose();
            Assert.That(world.IsDisposed, Is.False);
            foreach (var operation in Accessors(store, binding))
                Assert.Throws<ObjectDisposedException>(() => operation());
            Assert.DoesNotThrow(() => store.Dispose());
            using var afterWorld = new InterpolatedHistoryStore<State>(world);
            var otherBinding = afterWorld.Bind(binding.Entity);
            world.Dispose();
            foreach (var operation in Accessors(afterWorld, otherBinding))
                Assert.Throws<ObjectDisposedException>(() => operation());
            Assert.Throws<ObjectDisposedException>(() => new InterpolatedHistoryStore<State>(world));
            Assert.DoesNotThrow(() => afterWorld.Dispose());
        }

        [Test]
        public void WarmAddSampleEvictUnbindRebindResetAndNoOpReserveAllocateNothing()
        {
            using var world = new World();
            using var store = new InterpolatedHistoryStore<State>(world, historyLength: 3);
            var entity = Spawn(world);
            var ten = new State { Value = 10 };
            var twenty = new State { Value = 20 };
            var twentyFive = new State { Value = 25 };
            var thirty = new State { Value = 30 };
            var forty = new State { Value = 40 };
            int successes = 0;
            TestDelegate body = () =>
            {
                successes = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var binding = store.Bind(entity);
                    if (store.TryAdd(in binding, 20, in twenty) == HistoryWriteResult.Accepted) successes++;
                    if (store.TryAdd(in binding, 10, in ten) == HistoryWriteResult.Accepted) successes++;
                    if (store.TryAdd(in binding, 30, in thirty) == HistoryWriteResult.Accepted) successes++;
                    if (store.TryAdd(in binding, 25, in twentyFive) == HistoryWriteResult.Accepted) successes++;
                    if (store.TryAdd(in binding, 40, in forty) == HistoryWriteResult.Accepted) successes++;
                    if (store.TryAdd(in binding, 1, in ten) == HistoryWriteResult.OutsideWindow) successes++;
                    if (store.TryAdd(in binding, 30, in ten) == HistoryWriteResult.DuplicateTick) successes++;
                    if (store.TrySample(in binding, 30, 0.5, out var copy) && Math.Abs(copy.Value - 30.5) < 0.00001) successes++;
                    if (store.TrySample(in binding, 0, 0, out copy) && copy.Value == 25) successes++;
                    if (store.Unbind(in binding)) successes++;
                }
                store.EnsureCapacity(0);
                store.ResetTimeline();
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(successes, Is.EqualTo(10000));
        }
    }
}
