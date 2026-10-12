using System;
using System.Collections.Generic;
using NUnit.Framework;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Tests.Ecs
{
    public class SystemGroupTests
    {
        private sealed class CallbackSystem : ISystem
        {
            public Action<World, CommandBuffer, EcsTime> Callback;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => Callback(world, commands, time);
        }

        [Test]
        public void ExplicitTime_IsPassedUnchangedToEverySystem()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            var expected = new EcsTime(123, 0.123456789012345, 1.0 / 30, 0.765432109876543, true);
            var received = new List<EcsTime>();
            group.Add(new CallbackSystem { Callback = (w, c, time) => received.Add(time) });
            group.Add(new CallbackSystem { Callback = (w, c, time) => received.Add(time) });
            group.Update(in expected);
            Assert.That(received.Count, Is.EqualTo(2));
            foreach (var actual in received)
            {
                Assert.That(actual.Tick, Is.EqualTo(expected.Tick));
                Assert.That(actual.DeltaTime, Is.EqualTo(expected.DeltaTime));
                Assert.That(actual.TickInterval, Is.EqualTo(expected.TickInterval));
                Assert.That(actual.Alpha, Is.EqualTo(expected.Alpha));
                Assert.That(actual.ElapsedTime, Is.EqualTo(expected.ElapsedTime));
                Assert.That(actual.IsReplay, Is.True);
            }
        }

        [Test]
        public void ConsumedClockTicks_ReachSystemsInOrderWithoutImplicitAdvance()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            var ticks = new List<uint>();
            group.Add(new CallbackSystem { Callback = (w, c, time) => ticks.Add(time.Tick) });
            var clock = new TickClock();
            Assert.That(clock.Accumulate(2.5 / 30), Is.EqualTo(2));
            Assert.That(ticks, Is.Empty);
            while (clock.TryAdvance(out var snapshot)) group.Update(in snapshot);
            Assert.That(ticks, Is.EqualTo(new uint[] { 1, 2 }));
            Assert.That(clock.Tick, Is.EqualTo(2));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.Alpha, Is.EqualTo(0.5).Within(1e-12));
        }

        [TestCase(0f)]
        [TestCase(0.1f)]
        [TestCase(float.MaxValue)]
        public void FloatConvenience_PassesUnscheduledFrameTime(float deltaTime)
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            EcsTime actual = default;
            int calls = 0;
            group.Add(new CallbackSystem { Callback = (w, c, time) => { actual = time; calls++; } });
            group.Update(deltaTime);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(actual.DeltaTime, Is.EqualTo((double)deltaTime));
            Assert.That(actual.Tick, Is.Zero);
            Assert.That(actual.TickInterval, Is.Zero);
            Assert.That(actual.Alpha, Is.Zero);
            Assert.That(actual.ElapsedTime, Is.Zero);
            Assert.That(actual.IsReplay, Is.False);
        }

        [Test]
        public void DefaultTime_IsAValidZeroDurationFrame()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            int calls = 0;
            group.Add(new CallbackSystem { Callback = (w, c, time) => { calls++; Assert.That(time.DeltaTime, Is.Zero); } });
            var frame = default(EcsTime);
            group.Update(in frame);
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TimeDispatch_AllocatesNothingAfterWarmup(bool useFloatConvenience)
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            int calls = 0;
            group.Add(new CallbackSystem { Callback = (w, c, time) => calls++ });
            var snapshot = new EcsTime(1, 1.0 / 30, 1.0 / 30);
            int iterations = 1;
            TestDelegate body = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (useFloatConvenience) group.Update(0.125f);
                    else group.Update(in snapshot);
                }
            };
            for (int i = 0; i < 32; i++) body();
            calls = 0;
            iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(calls, Is.EqualTo(10000));
        }

        [Test]
        public void BorrowedCommandBuffer_CannotBeDisposedBySystemOrRetainedCaller()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            CommandBuffer retained = null;
            group.Add(new CallbackSystem
            {
                Callback = (w, c, dt) =>
                {
                    retained = c;
                    Assert.Throws<InvalidOperationException>(() => c.Dispose());
                    c.Set(c.CreateEntity(), new Health());
                }
            });
            Assert.DoesNotThrow(() => group.Update(EcsTime.ForFrame(1)));
            Assert.Throws<InvalidOperationException>(() => retained.Dispose());
            Assert.DoesNotThrow(() => group.Update(EcsTime.ForFrame(1)));
            Assert.That(world.EntityCount, Is.EqualTo(2));
        }

        [Test]
        public void Systems_OrderIsStableAndChangesAreVisibleToNextSystem()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            var order = new List<int>();
            var observe = new CallbackSystem
            {
                Callback = (w, c, dt) =>
                {
                    order.Add(2);
                    Assert.That(w.Query().WithAll<Health>().Count, Is.EqualTo(1));
                    Assert.That(dt.DeltaTime, Is.EqualTo(0.25));
                }
            };
            var spawn = new CallbackSystem
            {
                Callback = (w, c, dt) =>
                {
                    order.Add(1);
                    c.Set(c.CreateEntity(), new Health());
                }
            };
            var last = new CallbackSystem { Callback = (w, c, dt) => order.Add(3) };
            group.Add(observe, 10); group.Add(spawn, 0); group.Add(last, 10);
            group.Update(new EcsTime(1, 0.25, 0.25));
            Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void Systems_EnableRemoveAndDuplicateRegistrationAreExplicit()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            int calls = 0;
            var system = new CallbackSystem { Callback = (w, c, dt) => calls++ };
            group.Add(system);
            Assert.Throws<InvalidOperationException>(() => group.Add(system));
            group.SetEnabled(system, false);
            group.Update(EcsTime.ForFrame(1));
            Assert.That(calls, Is.Zero);
            group.SetEnabled(system, true);
            group.Update(EcsTime.ForFrame(1));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(group.Remove(system), Is.True);
            Assert.That(group.Remove(system), Is.False);
            Assert.Throws<InvalidOperationException>(() => group.SetEnabled(system, true));
        }

        [Test]
        public void SystemExecution_RejectsReentrancyAndDirectStructuralMutation()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            CallbackSystem system = null;
            system = new CallbackSystem
            {
                Callback = (w, c, dt) =>
                {
                    Assert.Throws<InvalidOperationException>(() => group.Update(EcsTime.ForFrame(1)));
                    Assert.Throws<InvalidOperationException>(() => group.Update(1f));
                    Assert.Throws<InvalidOperationException>(() => group.Remove(system));
                    Assert.Throws<InvalidOperationException>(() => group.Dispose());
                    Assert.Throws<InvalidOperationException>(() => w.CreateEntity());
                    Assert.Throws<InvalidOperationException>(() => c.Playback());
                }
            };
            group.Add(system);
            Assert.DoesNotThrow(() => group.Update(EcsTime.ForFrame(1)));
        }

        [Test]
        public void SystemException_DiscardsItsCommandsAndStopsLaterSystemsButGroupCanRecover()
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            int later = 0;
            var fail = new CallbackSystem
            {
                Callback = (w, c, dt) =>
                {
                    c.CreateEntity();
                    throw new ApplicationException();
                }
            };
            group.Add(fail);
            group.Add(new CallbackSystem { Callback = (w, c, dt) => later++ });
            Assert.Throws<ApplicationException>(() => group.Update(EcsTime.ForFrame(1)));
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(later, Is.Zero);
            group.Remove(fail);
            group.Update(EcsTime.ForFrame(1));
            Assert.That(later, Is.EqualTo(1));
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void InvalidTime_IsRejectedBeforeSystemsExecute(float deltaTime)
        {
            using var world = new World();
            using var group = new SystemGroup(world);
            int calls = 0;
            group.Add(new CallbackSystem { Callback = (w, c, time) => calls++ });
            Assert.Throws<ArgumentOutOfRangeException>(() => group.Update(deltaTime));
            Assert.That(calls, Is.Zero);
            group.Update(0f);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void DisposingGroup_DoesNotDisposeWorldOrCallerOwnedSystems()
        {
            using var world = new World();
            var group = new SystemGroup(world);
            group.Dispose(); group.Dispose();
            Assert.DoesNotThrow(() => world.CreateEntity());
            Assert.Throws<ObjectDisposedException>(() => group.Update(EcsTime.ForFrame(1)));
        }
    }
}
